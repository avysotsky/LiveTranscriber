using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LiveTranscriber.Core.Translation;

/// <summary>
/// Persistent local CPU Marian/OPUS-MT worker over stdin/stdout JSON-lines.
/// No HTTP client or network access is created by this provider.
/// </summary>
public sealed class LocalOpusMtTranslator : ITextTranslator
{
    private readonly Process _process;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private long _sequence;
    private bool _disposed;

    private LocalOpusMtTranslator(Process process) => _process = process;

    public static async Task<LocalOpusMtTranslator> StartAsync(
        string? modelDirectory = null,
        string? pythonExecutable = null,
        string? workerFile = null,
        CancellationToken cancellationToken = default)
    {
        string model = modelDirectory ?? Environment.GetEnvironmentVariable("LIVE_TRANSLATOR_MODEL_DIR") ??
            Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory) ?? "D:\\",
                "Models", "opus-mt-en-ru-ct2");
        string script = workerFile ?? Path.Combine(AppContext.BaseDirectory, "local_translate_worker.py");
        string python = pythonExecutable ?? Environment.GetEnvironmentVariable("LIVE_TRANSLATOR_PYTHON") ??
            "py";

        ValidateFiles(model, script);
        var info = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8
        };
        if (Path.GetFileNameWithoutExtension(python).Equals("py", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add("-3");
        info.ArgumentList.Add("-u");
        info.ArgumentList.Add(script);
        info.ArgumentList.Add(model);
        info.Environment["HF_HUB_OFFLINE"] = "1";
        info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";

        var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Could not launch the offline Python translator.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new InvalidOperationException(
                "Offline Python translator could not start. Set LIVE_TRANSLATOR_PYTHON to your Python venv python.exe.");
        }

        var translator = new LocalOpusMtTranslator(process);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90)); // Cold import + CPU model initialization.
            string? ready = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (ready is null || !IsReadyMessage(ready))
                throw new InvalidOperationException(
                    "Offline translation model did not initialize. Verify model setup and Python packages.");
            return translator;
        }
        catch
        {
            await translator.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static void ValidateFiles(string modelDirectory, string workerFile)
    {
        if (!File.Exists(Path.Combine(modelDirectory, "model.bin")) ||
            !File.Exists(Path.Combine(modelDirectory, "source.spm")) ||
            !File.Exists(Path.Combine(modelDirectory, "target.spm")))
            throw new InvalidOperationException(
                "Offline OPUS-MT model is missing. Run the local translator setup first.");
        if (!File.Exists(workerFile))
            throw new InvalidOperationException("Offline translation worker script was not installed.");
    }

    internal static bool IsReadyMessage(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("type", out var kind)
                && kind.GetString() == "ready";
        }
        catch (JsonException) { return false; }
    }

    public async Task<string> TranslateToRussianAsync(string englishText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(englishText)) return string.Empty;
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            long id = Interlocked.Increment(ref _sequence);
            string request = JsonSerializer.Serialize(new { id, text = englishText });
            await _process.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            string? line = await _process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line is null) throw new InvalidOperationException("Offline translation worker exited.");
            using var doc = JsonDocument.Parse(line);
            var body = doc.RootElement;
            if (!body.TryGetProperty("id", out var responseId) ||
                responseId.ValueKind != JsonValueKind.Number || responseId.GetInt64() != id ||
                !body.TryGetProperty("translation", out var translated) ||
                translated.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Offline translator returned an invalid response.");
            string result = translated.GetString()?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(result))
                throw new InvalidOperationException("Offline translator returned no translation.");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Offline translation timed out. Check available CPU and model installation.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Offline translation protocol error.");
        }
        catch (IOException)
        {
            throw new InvalidOperationException("Offline translation worker is unavailable.");
        }
        finally { _mutex.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                   or TimeoutException) { }
        finally
        {
            _process.Dispose();
            _mutex.Dispose();
        }
    }
}
