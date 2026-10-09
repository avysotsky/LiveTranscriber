using System.Diagnostics;
using System.Threading.Channels;

namespace LiveTranscriber.Core;

/// <summary>Bounded capture-to-ASR pipeline. Capture callbacks never wait for inference.</summary>
public sealed class TranscriptionSession : IAsyncDisposable
{
    private const double SamplesPerSecond = 16_000d;
    private const int QueueCapacity = 12;
    private const int QueueHighWatermark = 9; // 75% of the fixed 12-chunk buffer
    private readonly IAudioSource _source;
    private readonly ISpeechEngine _engine;
    private readonly object _queueGate = new();
    private readonly record struct CapturedFrame(float[] Samples, long CapturedAtTicks);
    private readonly Channel<CapturedFrame> _queue = Channel.CreateBounded<CapturedFrame>(new BoundedChannelOptions(QueueCapacity)
    {
        // The producer can evict the oldest frame when the queue is full.
        SingleReader = false,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private Task? _consumer;
    private int _running;
    private int _started;
    private long _droppedChunks;
    private long _droppedSamples;
    private long _queuedSamples;
    private long _peakQueuedSamples;
    private long _processedSamples;
    private long _processedChunks;
    private long _processorTicks;
    private long _queueWaitTicks;
    private long _peakQueueWaitTicks;
    private long _peakProcessorCallTicks;
    private int _queuedChunks;
    private int _peakQueuedChunks;
    private long _nearCapacityEvents;
    private bool _queueWasNearCapacity;

    public event Action<TranscriptUpdate>? TextAvailable;
    public event Action<Exception>? Failed;

    public TranscriptionSession(IAudioSource source, ISpeechEngine engine)
    {
        _source = source;
        _engine = engine;
        _source.SamplesCaptured += OnSamples;
        _source.Failed += OnFailed;
        _engine.TextAvailable += OnText;
        _engine.Failed += OnFailed;
    }

    public long DroppedChunks => Interlocked.Read(ref _droppedChunks);

    public double ProcessedAudioSeconds => Interlocked.Read(ref _processedSamples) / SamplesPerSecond;

    /// <summary>
    /// Total client-side processing time / processed audio duration.
    /// For cloud engines this measures client audio writes, NOT server-side speech latency.
    /// </summary>
    public double ProcessingRatio
    {
        get
        {
            long count = Interlocked.Read(ref _processedSamples);
            return count == 0 ? 0 : Interlocked.Read(ref _processorTicks) /
                (double)Stopwatch.Frequency / (count / SamplesPerSecond);
        }
    }

    public PipelineMetrics GetMetrics()
    {
        long queued, peak, dropped, pressureEvents;
        int chunks, peakChunks;
        lock (_queueGate)
        {
            queued = _queuedSamples;
            peak = _peakQueuedSamples;
            dropped = _droppedSamples;
            chunks = _queuedChunks;
            peakChunks = _peakQueuedChunks;
            pressureEvents = _nearCapacityEvents;
        }

        return new PipelineMetrics(
            DroppedChunks: Interlocked.Read(ref _droppedChunks),
            DroppedAudioSeconds: dropped / SamplesPerSecond,
            QueuedAudioSeconds: queued / SamplesPerSecond,
            PeakQueuedAudioSeconds: peak / SamplesPerSecond,
            ProcessedAudioSeconds: ProcessedAudioSeconds,
            ProcessingRatio: ProcessingRatio,
            ProcessedChunks: Interlocked.Read(ref _processedChunks),
            QueuedChunks: chunks,
            PeakQueuedChunks: peakChunks,
            QueueCapacityChunks: QueueCapacity,
            NearCapacityEvents: pressureEvents,
            AverageQueueWaitMilliseconds: Math.Max(0, Interlocked.Read(ref _queueWaitTicks) * 1000d /
                Stopwatch.Frequency / Math.Max(1, Interlocked.Read(ref _processedChunks))),
            PeakQueueWaitMilliseconds: Interlocked.Read(ref _peakQueueWaitTicks) * 1000d / Stopwatch.Frequency,
            PeakRecognizerCallMilliseconds: Interlocked.Read(ref _peakProcessorCallTicks) * 1000d / Stopwatch.Frequency);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A session can only be started once.");

        Volatile.Write(ref _running, 1);
        try
        {
            await _engine.StartAsync(cancellationToken).ConfigureAwait(false);
            _consumer = Task.Run(ConsumeAsync);
            _source.Start();
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ConsumeAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                CapturedFrame frame;
                lock (_queueGate)
                {
                    if (!_queue.Reader.TryRead(out frame)) continue;
                    _queuedSamples -= frame.Samples.Length;
                    _queuedChunks--;
                    if (_queuedChunks < QueueHighWatermark) _queueWasNearCapacity = false;
                }

                long begin = Stopwatch.GetTimestamp();
                long queueWait = Math.Max(0, begin - frame.CapturedAtTicks);
                try { await _engine.ProcessAsync(frame.Samples).ConfigureAwait(false); }
                finally
                {
                    long processorTicks = Math.Max(0, Stopwatch.GetTimestamp() - begin);
                    Interlocked.Add(ref _processorTicks, processorTicks);
                    Interlocked.Add(ref _queueWaitTicks, queueWait);
                    UpdatePeak(ref _peakQueueWaitTicks, queueWait);
                    UpdatePeak(ref _peakProcessorCallTicks, processorTicks);
                    Interlocked.Add(ref _processedSamples, frame.Samples.Length);
                    Interlocked.Increment(ref _processedChunks);
                }
            }
        }
        catch (Exception ex) { OnFailed(ex); }
    }

    private void OnSamples(float[] samples)
    {
        if (samples.Length == 0) return;
        var frame = new CapturedFrame(samples, Stopwatch.GetTimestamp());
        lock (_queueGate)
        {
            if (Volatile.Read(ref _running) == 0) return;

            if (!_queue.Writer.TryWrite(frame))
            {
                // Prefer the freshest audio and count exactly how much speech was lost.
                if (_queue.Reader.TryRead(out CapturedFrame oldest))
                {
                    _queuedSamples -= oldest.Samples.Length;
                    _queuedChunks--;
                    if (_queuedChunks < QueueHighWatermark) _queueWasNearCapacity = false;
                    _droppedSamples += oldest.Samples.Length;
                    Interlocked.Increment(ref _droppedChunks);
                }
                if (!_queue.Writer.TryWrite(frame))
                {
                    _droppedSamples += samples.Length;
                    Interlocked.Increment(ref _droppedChunks);
                    return;
                }
            }
            _queuedSamples += samples.Length;
            _queuedChunks++;
            _peakQueuedSamples = Math.Max(_peakQueuedSamples, _queuedSamples);
            _peakQueuedChunks = Math.Max(_peakQueuedChunks, _queuedChunks);
            if (_queuedChunks >= QueueHighWatermark && !_queueWasNearCapacity)
            {
                _nearCapacityEvents++;
                _queueWasNearCapacity = true;
            }
        }
    }

    private static void UpdatePeak(ref long target, long value)
    {
        long current;
        while ((current = Interlocked.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    private void OnText(TranscriptUpdate update) => TextAvailable?.Invoke(update);
    private void OnFailed(Exception ex) => Failed?.Invoke(ex);

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0) return;
        try { _source.Stop(); }
        catch (Exception ex) { OnFailed(ex); }

        lock (_queueGate) _queue.Writer.TryComplete();
        if (_consumer is not null) await _consumer.ConfigureAwait(false);
        try { await _engine.StopAsync().ConfigureAwait(false); }
        catch (Exception ex) { OnFailed(ex); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _source.SamplesCaptured -= OnSamples;
        _source.Failed -= OnFailed;
        _engine.TextAvailable -= OnText;
        _engine.Failed -= OnFailed;
        _source.Dispose();
        await _engine.DisposeAsync().ConfigureAwait(false);
    }
}
