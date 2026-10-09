using System.Threading.Channels;
using System.Diagnostics;

namespace LiveTranscriber.Core;

/// <summary>Bounded audio pipeline. Audio capture callbacks must never wait for inference.</summary>
public sealed class TranscriptionSession : IAsyncDisposable
{
    private readonly IAudioSource _source;
    private readonly ISpeechEngine _engine;
    private readonly Channel<float[]> _queue = Channel.CreateBounded<float[]>(new BoundedChannelOptions(12)
    {
        SingleReader = false, // Producer may also read to evict the oldest chunk.
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private Task? _consumer;
    private int _running;
    private int _started;
    private long _dropped;
    private long _processedSamples;
    private long _processorTicks;

    public long DroppedChunks => Interlocked.Read(ref _dropped);

    /// <summary>Seconds of audio processed by the recognizer.</summary>
    public double ProcessedAudioSeconds => Interlocked.Read(ref _processedSamples) / 16000d;

    /// <summary>
    /// CPU-side recognizer processing time divided by audio duration.
    /// For cloud this measures only upload/write time, NOT server-side ASR latency.
    /// </summary>
    public double ProcessingRatio
    {
        get
        {
            long frames = Interlocked.Read(ref _processedSamples);
            if (frames == 0) return 0;
            return Interlocked.Read(ref _processorTicks) / (double)Stopwatch.Frequency / (frames / 16000d);
        }
    }
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

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A session can only be started once.");
        Volatile.Write(ref _running, 1);
        try
        {
            await _engine.StartAsync(cancellationToken).ConfigureAwait(false);
            _consumer = Task.Run(async () =>
            {
                try
                {
                    await foreach (float[] samples in _queue.Reader.ReadAllAsync())
                        {
                        long begin = Stopwatch.GetTimestamp();
                        try { await _engine.ProcessAsync(samples).ConfigureAwait(false); }
                        finally
                        {
                            Interlocked.Add(ref _processorTicks, Stopwatch.GetTimestamp() - begin);
                            Interlocked.Add(ref _processedSamples, samples.Length);
                        }
                    }
                }
                catch (Exception ex) { OnFailed(ex); }
            });
            _source.Start();
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void OnSamples(float[] samples)
    {
        if (Volatile.Read(ref _running) == 0 || samples.Length == 0) return;
        if (_queue.Writer.TryWrite(samples)) return;
        // Drop stale speech rather than allowing unbounded latency.
        if (_queue.Reader.TryRead(out _)) Interlocked.Increment(ref _dropped);
        if (!_queue.Writer.TryWrite(samples)) Interlocked.Increment(ref _dropped);
    }

    private void OnText(TranscriptUpdate update) => TextAvailable?.Invoke(update);
    private void OnFailed(Exception ex) => Failed?.Invoke(ex);

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0) return;
        try { _source.Stop(); }
        catch (Exception ex) { OnFailed(ex); }
        _queue.Writer.TryComplete();
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
