using LiveTranscriber.Core;
using Xunit;

namespace LiveTranscriber.Core.Tests;

public sealed class TranscriptionSessionTests
{
    [Fact]
    public async Task DoesNotCaptureAudioBeforeStart()
    {
        var source = new FakeSource();
        var engine = new FakeEngine();
        await using var session = new TranscriptionSession(source, engine);
        Assert.False(source.IsStarted);
        Assert.False(engine.IsStarted);
    }

    [Fact]
    public async Task DeliversSamplesAndFinalTranscript()
    {
        var source = new FakeSource();
        var engine = new FakeEngine();
        await using var session = new TranscriptionSession(source, engine);
        var result = new TaskCompletionSource<TranscriptUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TextAvailable += value => result.TrySetResult(value);

        await session.StartAsync();
        source.Emit(new float[1600]);
        var update = await result.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Hello from test", update.Text);
        Assert.True(update.IsFinal);
        await session.StopAsync();
        Assert.False(source.IsStarted);
        Assert.False(engine.IsStarted);
    }

    [Fact]
    public async Task CannotStartTheSameSessionTwice()
    {
        var source = new FakeSource();
        var engine = new FakeEngine();
        await using var session = new TranscriptionSession(source, engine);
        await session.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
        await session.StopAsync();
    }

    private sealed class FakeSource : IAudioSource
    {
        public bool IsStarted { get; private set; }
        public event Action<float[]>? SamplesCaptured;
        public event Action<Exception>? Failed;
        public void Emit(float[] samples) => SamplesCaptured?.Invoke(samples);
        public void Start() => IsStarted = true;
        public void Stop() => IsStarted = false;
        public void Dispose() => Stop();
    }

    private sealed class FakeEngine : ISpeechEngine
    {
        public bool IsStarted { get; private set; }
        public event Action<TranscriptUpdate>? TextAvailable;
        public event Action<Exception>? Failed;
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsStarted = true;
            return Task.CompletedTask;
        }
        public ValueTask ProcessAsync(float[] samples, CancellationToken cancellationToken = default)
        {
            TextAvailable?.Invoke(new TranscriptUpdate("Hello from test", true));
            return ValueTask.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsStarted = false;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
