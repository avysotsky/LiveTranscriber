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
        source.Send(new float[160]); // 10ms packet occupies the consumer.
        await recognizer.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        for (int i = 0; i < 32; i++)
            source.Send(new float[160]);

        PipelineMetrics measured = sut.GetMetrics();
        Assert.Equal(8, measured.DroppedChunks);
        Assert.InRange(measured.DroppedAudioSeconds, 0.0799, 0.0801);
        Assert.InRange(measured.QueuedAudioSeconds, 0.2399, 0.2401);
        Assert.InRange(measured.PeakQueuedAudioSeconds, 0.2399, 0.2401);
        Assert.Equal(24, measured.QueuedChunks);
        Assert.Equal(24, measured.PeakQueuedChunks);
        Assert.Equal(24, measured.QueueCapacityChunks);
        Assert.Equal(1, measured.NearCapacityEvents);

        recognizer.ReleaseFirst.TrySetResult();
        await sut.StopAsync();
        PipelineMetrics after = sut.GetMetrics();
        Assert.Equal(0, after.QueuedAudioSeconds);
        Assert.Equal(25, after.ProcessedChunks);
        Assert.InRange(after.ProcessedAudioSeconds, 0.2499, 0.2501);
    }

    [Fact]
    public async Task FourteenPacketsOfBurstHeadroomProduceNoDrop()
    {
        var source = new SyntheticSource();
        var recognizer = new BlockFirstSpeechEngine();
        await using var sut = new TranscriptionSession(source, recognizer);
        await sut.StartAsync();
        source.Send(new float[160]);
        await recognizer.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Equivalent to ~140ms of incoming audio while the recognizer is blocked.
        for (int i = 0; i < 14; i++)
            source.Send(new float[160]);

        PipelineMetrics snapshot = sut.GetMetrics();
        Assert.Equal(24, snapshot.QueueCapacityChunks);
        Assert.Equal(14, snapshot.QueuedChunks);
        Assert.InRange(snapshot.QueuedAudioSeconds, 0.1399, 0.1401);
        Assert.Equal(0, snapshot.DroppedChunks);
        Assert.Equal(0, snapshot.NearCapacityEvents);

        recognizer.ReleaseFirst.TrySetResult();
        await sut.StopAsync();
        Assert.Equal(15, sut.GetMetrics().ProcessedChunks);
    }

    [Fact]
    public async Task DetectsNearCapacityBeforeAnyAudioIsDropped()
    {
        var source = new SyntheticSource();
        var recognizer = new BlockFirstSpeechEngine();
        await using var sut = new TranscriptionSession(source, recognizer);
        await sut.StartAsync();
        source.Send(new float[160]); // 10ms frame already being processed.
        await recognizer.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        for (int i = 0; i < 18; i++)
            source.Send(new float[160]);

        PipelineMetrics active = sut.GetMetrics();
        Assert.Equal(18, active.QueuedChunks);
        Assert.Equal(24, active.QueueCapacityChunks);
        Assert.Equal(1, active.NearCapacityEvents);
        Assert.Equal(0, active.DroppedChunks);
        Assert.InRange(active.QueuedAudioSeconds, 0.1799, 0.1801);

        recognizer.ReleaseFirst.TrySetResult();
        await sut.StopAsync();
        Assert.Equal(0, sut.GetMetrics().QueuedChunks);
    }

    [Fact]
    public async Task QueueBelowHighWatermarkDoesNotReportPressure()
    {
        var source = new SyntheticSource();
        var recognizer = new BlockFirstSpeechEngine();
        await using var sut = new TranscriptionSession(source, recognizer);
        await sut.StartAsync();
        source.Send(new float[160]);
        await recognizer.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 17; i++)
            source.Send(new float[160]);

        Assert.Equal(17, sut.GetMetrics().QueuedChunks);
        Assert.Equal(0, sut.GetMetrics().NearCapacityEvents);
        recognizer.ReleaseFirst.TrySetResult();
        await sut.StopAsync();
    }

    [Fact]
    public async Task MeasuresCaptureCallbackToRecognizerStartWaitUnderBackpressure()
    {
        var source = new SyntheticSource();
        var recognizer = new BlockFirstSpeechEngine();
        await using var sut = new TranscriptionSession(source, recognizer);
        await sut.StartAsync();

        source.Send(new float[160]); // First call blocks the processing worker.
        await recognizer.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Send(new float[160]); // Queue arrival timestamp begins here.
        await Task.Delay(80); // Simulate a busy recognizer without sleeping the capture callback.

        PipelineMetrics queued = sut.GetMetrics();
        Assert.Equal(1, queued.QueuedChunks);
        Assert.Equal(0, queued.DroppedChunks);

        recognizer.ReleaseFirst.TrySetResult();
        await sut.StopAsync();

        PipelineMetrics finished = sut.GetMetrics();
        Assert.Equal(2, finished.ProcessedChunks);
        Assert.Equal(0, finished.QueuedChunks);
        Assert.True(finished.PeakQueueWaitMilliseconds >= 60,
            $"Expected observable queue wait after 80ms block, got {finished.PeakQueueWaitMilliseconds}ms.");
        Assert.True(finished.AverageQueueWaitMilliseconds >= 25);
        Assert.True(finished.PeakRecognizerCallMilliseconds >= 60);
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
        Assert.Equal(0, sut.GetMetrics().PeakQueueWaitMilliseconds);
        Assert.Equal(0, sut.GetMetrics().PeakRecognizerCallMilliseconds);
    }

    private sealed class SyntheticSource : IAudioSource
    {
        public event Action<float[]>? SamplesCaptured;
        public event Action<Exception>? Failed;
        public void Send(float[] sample) => SamplesCaptured?.Invoke(sample);
        public void SignalFailure(Exception error) => Failed?.Invoke(error);
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
        public void EmitText(string text) => TextAvailable?.Invoke(new TranscriptUpdate(text, true));
        public void SignalFailure(Exception error) => Failed?.Invoke(error);

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
