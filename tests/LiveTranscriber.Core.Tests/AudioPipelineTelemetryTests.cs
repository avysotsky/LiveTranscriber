using LiveTranscriber.Core;
using Xunit;

namespace LiveTranscriber.Core.Tests;

public sealed class AudioPipelineTelemetryTests
{
    [Fact]
    public async Task OverflowDiscardsOldestAudioAndMeasuresLostDuration()
    {
        var source = new SyntheticSource();
        var recognizer = new BlockFirstSpeechEngine();
        await using var sut = new TranscriptionSession(source, recognizer);
        await sut.StartAsync();
        source.Send(new float[1600]); // Occupy consumer.
        await recognizer.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        for (int i = 0; i < 20; i++)
            source.Send(new float[1600]);

        PipelineMetrics measured = sut.GetMetrics();
        Assert.Equal(8, measured.DroppedChunks);
        Assert.InRange(measured.DroppedAudioSeconds, 0.799, 0.801);
        Assert.InRange(measured.QueuedAudioSeconds, 1.199, 1.201);
        Assert.InRange(measured.PeakQueuedAudioSeconds, 1.199, 1.201);

        recognizer.ReleaseFirst.TrySetResult();
        await sut.StopAsync();
        PipelineMetrics after = sut.GetMetrics();
        Assert.Equal(0, after.QueuedAudioSeconds);
        Assert.Equal(13, after.ProcessedChunks);
        Assert.InRange(after.ProcessedAudioSeconds, 1.299, 1.301);
    }

    [Fact]
    public async Task EmptyFramesAreIgnoredAndDoNotCreateFalseMetrics()
    {
        var source = new SyntheticSource();
        var recognizer = new BlockFirstSpeechEngine();
        await using var sut = new TranscriptionSession(source, recognizer);
        await sut.StartAsync();
        source.Send([]);
        await sut.StopAsync();
        Assert.Equal(0, sut.GetMetrics().ProcessedAudioSeconds);
        Assert.Equal(0, sut.GetMetrics().DroppedAudioSeconds);
    }

    private sealed class SyntheticSource : IAudioSource
    {
        public event Action<float[]>? SamplesCaptured;
        public event Action<Exception>? Failed;
        public void Send(float[] sample) => SamplesCaptured?.Invoke(sample);
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class BlockFirstSpeechEngine : ISpeechEngine
    {
        private int _calls;
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<TranscriptUpdate>? TextAvailable;
        public event Action<Exception>? Failed;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async ValueTask ProcessAsync(float[] samples, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstEntered.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
