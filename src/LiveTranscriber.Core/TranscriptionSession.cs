using System.Threading.Channels;

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

    public long DroppedChunks => Interlocked.Read(ref _dropped);
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
                        await _engine.ProcessAsync(samples).ConfigureAwait(false);
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
