using System.IO;
using LiveTranscriber.Core;
using SherpaOnnx;

namespace LiveTranscriber.Desktop.Engines;

/// <summary>Offline streaming Zipformer INT8 recognition with two inference threads.</summary>
public sealed class SherpaLocalSpeechEngine : ISpeechEngine
{
    private OnlineRecognizer? _recognizer;
    private OnlineStream? _stream;
    private string _previous = string.Empty;

    public event Action<TranscriptUpdate>? TextAvailable;
    public event Action<Exception>? Failed;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        string directory = Environment.GetEnvironmentVariable("LIVE_TRANSCRIBER_MODEL_DIR") ??
            Path.Combine(AppContext.BaseDirectory, "models", "sherpa-onnx-streaming-zipformer-en-2023-06-21");
        string tokens = Path.Combine(directory, "tokens.txt");
        string encoder = Path.Combine(directory, "encoder-epoch-99-avg-1.int8.onnx");
        string decoder = Path.Combine(directory, "decoder-epoch-99-avg-1.onnx");
        string joiner = Path.Combine(directory, "joiner-epoch-99-avg-1.int8.onnx");
        if (new[] { tokens, encoder, decoder, joiner }.Any(path => !File.Exists(path)))
            throw new FileNotFoundException($"Model files are missing from {directory}. See README.");

        await Task.Run(() =>
        {
            var cfg = new OnlineRecognizerConfig();
            cfg.FeatConfig.SampleRate = 16000;
            cfg.FeatConfig.FeatureDim = 80;
            cfg.ModelConfig.Transducer.Encoder = encoder;
            cfg.ModelConfig.Transducer.Decoder = decoder;
            cfg.ModelConfig.Transducer.Joiner = joiner;
            cfg.ModelConfig.Tokens = tokens;
            cfg.ModelConfig.Provider = "cpu";
            cfg.ModelConfig.NumThreads = 2;
            cfg.DecodingMethod = "greedy_search";
            cfg.EnableEndpoint = 1;
            cfg.Rule2MinTrailingSilence = 0.8f;
            _recognizer = new OnlineRecognizer(cfg);
            _stream = _recognizer.CreateStream();
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask ProcessAsync(float[] samples, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_recognizer is null || _stream is null) throw new InvalidOperationException("Local ASR is not started.");
        _stream.AcceptWaveform(16000, samples);
        while (_recognizer.IsReady(_stream))
            _recognizer.Decode(_stream);

        string text = _recognizer.GetResult(_stream).Text.Trim();
        if (text.Length > 0 && text != _previous)
        {
            _previous = text;
            TextAvailable?.Invoke(new TranscriptUpdate(text, false));
        }
        if (_recognizer.IsEndpoint(_stream))
        {
            if (text.Length > 0) TextAvailable?.Invoke(new TranscriptUpdate(text, true));
            _recognizer.Reset(_stream);
            _previous = string.Empty;
        }
        return ValueTask.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_recognizer is null || _stream is null) return;
        await Task.Run(() =>
        {
            try
            {
                _stream.AcceptWaveform(16000, new float[16_000]);
                _stream.InputFinished();
                while (_recognizer.IsReady(_stream))
                    _recognizer.Decode(_stream);
                string final = _recognizer.GetResult(_stream).Text.Trim();
                if (final.Length > 0) TextAvailable?.Invoke(new TranscriptUpdate(final, true));
            }
            catch (Exception ex) { Failed?.Invoke(ex); }
            finally
            {
                _stream.Dispose();
                _recognizer.Dispose();
                _stream = null;
                _recognizer = null;
            }
        }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
