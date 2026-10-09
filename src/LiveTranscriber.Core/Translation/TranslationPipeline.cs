using System.Threading.Channels;

namespace LiveTranscriber.Core.Translation;

/// <summary>Non-content metrics for explicit Groq text translation.</summary>
public sealed record TranslationMetrics(
    long FinalPhrasesQueued,
    long PreviewPhrasesQueued,
    long ApiRequestsStarted,
    long ApiRequestsSucceeded,
    long ApiRequestsFailed,
    int PendingPhrases);

/// <summary>
/// ASR producers never wait for translation. Finalized phrases are translated in
/// original order, while provisional hypotheses can be superseded without display
/// of stale translations. Groq is only called by an explicitly opted-in instance.
/// </summary>
public sealed class TranslationPipeline : IAsyncDisposable
{
    private readonly record struct Phrase(string Text, long Generation, bool IsPreview, long PreviewRevision);
    private readonly ITextTranslator _translator;
    private readonly Channel<Phrase> _queue = Channel.CreateBounded<Phrase>(new BoundedChannelOptions(64)
    {
        SingleReader = false, // ClearPending can evict old phrases.
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private readonly TimeSpan _requestInterval;
    private readonly int _maxFinalBatch;
    private long _generation;
    private long _previewRevision;
    private int _accepting = 1;
    private int _pending;
    private Phrase? _carried;
    private long _finalQueued;
    private long _previewQueued;
    private long _requestsStarted;
    private long _requestsSucceeded;
    private long _requestsFailed;

    public event Action<string, long>? Translated;
    public event Action<string, long>? PreviewTranslated;
    public event Action<string>? Error;

    public long Generation => Interlocked.Read(ref _generation);
    public int PendingPhrases => Math.Max(0, Volatile.Read(ref _pending));

    public TranslationPipeline(ITextTranslator translator, TimeSpan? requestInterval = null,
        int maxFinalBatch = 4)
    {
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));
        _requestInterval = requestInterval ?? TimeSpan.FromMilliseconds(2200);
        if (_requestInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestInterval));
        if (maxFinalBatch is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(maxFinalBatch));
        _maxFinalBatch = maxFinalBatch;
        _worker = Task.Run(ConsumeAsync);
    }

    public TranslationMetrics GetMetrics() => new(
        Interlocked.Read(ref _finalQueued),
        Interlocked.Read(ref _previewQueued),
        Interlocked.Read(ref _requestsStarted),
        Interlocked.Read(ref _requestsSucceeded),
        Interlocked.Read(ref _requestsFailed),
        PendingPhrases);

    public bool TryEnqueueFinal(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        // A real ASR endpoint invalidates its previous *preview* translation.
        long revision = Interlocked.Increment(ref _previewRevision);
        bool added = Enqueue(new Phrase(text.Trim(), Generation, false, revision), true);
        if (added) Interlocked.Increment(ref _finalQueued);
        return added;
    }

    /// <summary>
    /// A confirmed *incremental* English chunk from ongoing speech. Keep
    /// earlier streamed tokens visible even when subsequent chunks are queued.
    /// Each segment must be committed separately (maxFinalBatch=1).
    /// </summary>
    public bool TryEnqueueLiveChunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        bool added = Enqueue(new Phrase(text.Trim(), Generation, false,
            Interlocked.Read(ref _previewRevision)), true);
        if (added) Interlocked.Increment(ref _finalQueued);
        return added;
    }

    /// <summary>
    /// Request a translation of the current unfinished utterance. The UI
    /// calls this at a controlled interval, not for every ASR token.
    /// Older previews are not shown once a newer hypothesis or final arrives.
    /// </summary>
    public bool TryEnqueuePreview(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || Volatile.Read(ref _accepting) == 0)
            return false;
        if (PendingPhrases >= 3) return false;
        long revision = Interlocked.Increment(ref _previewRevision);
        bool added = Enqueue(new Phrase(text.Trim(), Generation, true, revision), false);
        if (added) Interlocked.Increment(ref _previewQueued);
        return added;
    }

    private bool Enqueue(Phrase phrase, bool reportFull)
    {
        if (Volatile.Read(ref _accepting) == 0) return false;
        Interlocked.Increment(ref _pending);
        if (_queue.Writer.TryWrite(phrase)) return true;
        Interlocked.Decrement(ref _pending);
        if (reportFull) Error?.Invoke("Russian translation is lagging: the pending phrase queue is full.");
        return false;
    }

    public void ClearPending()
    {
        Interlocked.Increment(ref _generation);
        Interlocked.Increment(ref _previewRevision);
        while (_queue.Reader.TryRead(out _))
            Interlocked.Decrement(ref _pending);
        // The response to any already-sent request is ignored after Clear.
    }

    private async Task ConsumeAsync()
    {
        try
        {
            while (_carried is not null ||
                   await _queue.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
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

                if (first.Generation != Generation) continue;
                if (first.IsPreview &&
                    (first.PreviewRevision != Interlocked.Read(ref _previewRevision) ||
                     Volatile.Read(ref _accepting) == 0))
                    continue;

                var phrases = new List<string> { first.Text };
                if (!first.IsPreview)
                {
                    // A provisional result is never batched with finalized phrases.
                    while (phrases.Count < _maxFinalBatch && _queue.Reader.TryRead(out Phrase next))
                    {
                        Interlocked.Decrement(ref _pending);
                        if (!next.IsPreview && next.Generation == first.Generation)
                            phrases.Add(next.Text);
                        else
                        {
                            _carried = next;
                            break;
                        }
                    }
                }

                if (first.Generation != Generation) continue;
                try
                {
                    Interlocked.Increment(ref _requestsStarted);
                    string input = string.Join("\n", phrases);
                    string result;
                    if (_translator is IStreamingTextTranslator streaming)
                    {
                        result = await streaming.TranslateToRussianStreamingAsync(
                            input,
                            cumulative =>
                            {
                                // Partial output is transient even for finalized
                                // English input. Never append it as a final line.
                                // A newer preview, final event, or Clear invalidates
                                // stale in-flight streamed tokens.
                                if (first.Generation == Generation &&
                                    (!first.IsPreview || first.PreviewRevision ==
                                        Interlocked.Read(ref _previewRevision)) &&
                                    Volatile.Read(ref _accepting) != 0 &&
                                    !string.IsNullOrWhiteSpace(cumulative))
                                    PreviewTranslated?.Invoke(cumulative, first.Generation);
                            },
                            _shutdown.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        result = await _translator.TranslateToRussianAsync(
                            input, _shutdown.Token).ConfigureAwait(false);
                    }
                    Interlocked.Increment(ref _requestsSucceeded);

                    if (first.Generation == Generation && !string.IsNullOrWhiteSpace(result))
                    {
                        if (first.IsPreview)
                        {
                            if (first.PreviewRevision == Interlocked.Read(ref _previewRevision))
                                PreviewTranslated?.Invoke(result, first.Generation);
                        }
                        else Translated?.Invoke(result, first.Generation);
                    }
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // Retain the last visible Russian preview when a cloud
                    // request fails. Erasing it caused a blank translation
                    // panel for 10-15 seconds; error status is shown separately.
                    Interlocked.Increment(ref _requestsFailed);
                    if (first.Generation == Generation)
                        Error?.Invoke(ex is HttpRequestException or TaskCanceledException
                            ? "Groq connection failed or timed out."
                            : ex is InvalidOperationException
                                ? ex.Message
                                : "Groq returned an unreadable response or the translation request failed.");
                }

                if (_requestInterval > TimeSpan.Zero)
                    await Task.Delay(_requestInterval, _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    public async Task CompleteAsync(TimeSpan maxWait)
    {
        if (Interlocked.Exchange(ref _accepting, 0) != 0) _queue.Writer.TryComplete();
        try { await _worker.WaitAsync(maxWait).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _shutdown.Cancel();
            await _worker.ConfigureAwait(false);
            Error?.Invoke("Pending translations were cancelled after the stop timeout.");
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
