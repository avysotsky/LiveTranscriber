namespace LiveTranscriber.Core;

public sealed record TranscriptUpdate(string Text, bool IsFinal);

/// <summary>Source outputs mono float32 PCM sampled at 16 kHz. Do not open microphone devices.</summary>
public interface IAudioSource : IDisposable
{
    event Action<float[]>? SamplesCaptured;
    event Action<Exception>? Failed;
    void Start();
    void Stop();
}

/// <summary>One consumer calls Start, Process, and Stop sequentially.</summary>
public interface ISpeechEngine : IAsyncDisposable
{
    event Action<TranscriptUpdate>? TextAvailable;
    event Action<Exception>? Failed;
    Task StartAsync(CancellationToken cancellationToken = default);
    ValueTask ProcessAsync(float[] samples, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
