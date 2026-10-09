using LiveTranscriber.Core.Translation;
using Xunit;

namespace LiveTranscriber.Core.Tests;

public sealed class LocalOpusMtTranslatorTests
{
    [Fact]
    public async Task PersistentPythonWorkerTranslatesMultiplePhrasesWithoutHttp()
    {
        string folder = Path.Combine(Path.GetTempPath(), "lt-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string script = Path.Combine(folder, "mock_worker.py");
        try
        {
            File.WriteAllText(Path.Combine(folder, "model.bin"), "fake");
            File.WriteAllText(Path.Combine(folder, "source.spm"), "fake");
            File.WriteAllText(Path.Combine(folder, "target.spm"), "fake");
            File.WriteAllText(script, """
                import json
                import sys
                print(json.dumps({"type":"ready"}), flush=True)
                for line in sys.stdin:
                    data = json.loads(line)
                    print(json.dumps({"id": data["id"], "translation": "RU:" + data["text"]}, ensure_ascii=False), flush=True)
                """);

            string python = OperatingSystem.IsWindows() ? "py" : "python3";
            await using var sut = await LocalOpusMtTranslator.StartAsync(
                folder, python, script);
            Assert.Equal("RU:dependency injection",
                await sut.TranslateToRussianAsync("dependency injection", CancellationToken.None));
            Assert.Equal("RU:ASP.NET Core",
                await sut.TranslateToRussianAsync("ASP.NET Core", CancellationToken.None));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void MissingOfflineModelFailsClearlyWithoutOpeningNetwork()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LocalOpusMtTranslator.ValidateFiles(
                Path.Combine(Path.GetTempPath(), "missing-lt-opus-model"),
                "missing-worker.py"));
        Assert.Contains("setup", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"type\":\"ready\"}", true)]
    [InlineData("{\"type\":\"error\",\"error\":\"no model\"}", false)]
    [InlineData("not-json", false)]
    [InlineData("{}", false)]
    public void WorkerHandshakeIsStrictAndPrivate(string line, bool ready)
    {
        Assert.Equal(ready, LocalOpusMtTranslator.IsReadyMessage(line));
    }
}
