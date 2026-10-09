using System.Threading.Channels;

namespace LiveTranscriber.Core.Translation;

/// <summary>
/// One non-blocking producer, one ordered HTTP worker, batched finalized phrases.
/// Never delays the ASR worker, saves audio, or uploads anything before opt-in.
/// </summary>
public sealed class TranslationPipeline : IAsyncDisposable
{
    private readonly record struct Phrase(string Text, long Generation);
    private readonly ITextTranslator _translator;
    private readonly Channel<Phrase> _queue = Channel.CreateBounded<Phrase>(new BoundedChannelOptions(64)
    {
        SingleReader = false, // ClearPending may drain outstanding phrases.
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private readonly TimeSpan _requestInterval;
    private long _generation;
    private int _accepting = 1;
    private int _pending;
    private Phrase? _carried;

    public event Action<string, long>? Translated;
    public event Action<string>? Error;
    public long Generation => Interlocked.Read(ref _generation);
    public int PendingPhrases => Math.Max(0, Volatile.Read(ref _pending));

    public TranslationPipeline(ITextTranslator translator, TimeSpan? requestInterval = null)
    {
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));
        _requestInterval = requestInterval ?? TimeSpan.FromMilliseconds(2200);
        if (_requestInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestInterval));
        _worker = Task.Run(ConsumeAsync);
    }

    public bool TryEnqueueFinal(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (Volatile.Read(ref _accepting) == 0) return false;
        var item = new Phrase(text.Trim(), Generation);
        // We don't silently evict interview speech when translation falls behind.
        Interlocked.Increment(ref _pending);
        if (_queue.Writer.TryWrite(item)) return true;
        Interlocked.Decrement(ref _pending);
        Error?.Invoke("Russian translation is lagging: the pending phrase queue is full.");
        return false;
    }

    public void ClearPending()
    {
        Interlocked.Increment(ref _generation);
        while (_queue.Reader.TryRead(out _))
            Interlocked.Decrement(ref _pending);
        // A request already in flight may finish, but its generation is discarded.
    }

    private async Task ConsumeAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                Phrase first;
                if (_carried is { } carried)
                {
                    first = carried;
                    _carried = null;
                }
                else
                {
                    if (!_queue.Reader.TryRead(out first)) continue;
                    Interlocked.Decrement(ref _pending);
                }

                var phrases = new List<string> { first.Text };
                // Fold up to 4 speech endpoints into one Groq call when input is arriving
                // faster than the translation request rate. Order remains unchanged.
                while (phrases.Count < 4 && _queue.Reader.TryRead(out var next))
                {
                    Interlocked.Decrement(ref _pending);
                    if (next.Generation == first.Generation)
                        phrases.Add(next.Text);
                    else
                    {
                        // Do not lose the first utterance after ClearPending.
                        _carried = next;
                        break;
                    }
                }
                if (first.Generation != Generation) continue;

                try
                {
                    string result = await _translator.TranslateToRussianAsync(
                        string.Join("\n", phrases), _shutdown.Token).ConfigureAwait(false);
                    if (first.Generation == Generation && !string.IsNullOrWhiteSpace(result))
                        Translated?.Invoke(result, first.Generation);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    if (first.Generation == Generation)
                        Error?.Invoke(ex is HttpRequestException or TaskCanceledException
                            ? "Russian translation network request failed or timed out."
                            : ex.Message);
                }
                if (_requestInterval > TimeSpan.Zero)
                    await Task.Delay(_requestInterval, _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    /// <summary>Attempt to finish pending translations; cancel remaining requests after the deadline.</summary>
    public async Task CompleteAsync(TimeSpan maxWait)
    {
        if (Interlocked.Exchange(ref _accepting, 0) != 0) _queue.Writer.TryComplete();
        try { await _worker.WaitAsync(maxWait).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _shutdown.Cancel();
            await _worker.ConfigureAwait(false);
            Error?.Invoke("Some pending translations were cancelled after the stop timeout.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _accepting, 0);
        _queue.Writer.TryComplete();
        _shutdown.Cancel();
        try { await _worker.ConfigureAwait(false); }
        finally
        {
            await _translator.DisposeAsync().ConfigureAwait(false);
            _shutdown.Dispose();
        }
    }
}
