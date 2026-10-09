namespace LiveTranscriber.Core.Translation;

/// <summary>
/// Converts a continuously revised ASR utterance into small, ordered English
/// segments. Submitted segments are never requested again after an ASR endpoint.
/// A suffix is kept until the next stable word boundary; no speech data is saved.
/// </summary>
public sealed class IncrementalEnglishChunker
{
    private const int MinimumSegment = 34;
    private const int MaximumSegment = 105;
    private const int UnstableTail = 12;
    private readonly Queue<string> _ready = new();
    private int _consumed;

    public int PendingSegments => _ready.Count;

    public void Observe(string? english, bool isFinal)
    {
        string text = english?.Trim() ?? "";
        if (text.Length == 0)
        {
            if (isFinal) _consumed = 0;
            return;
        }

        if (_consumed > text.Length)
        {
            // ASR can revise its partial hypothesis. Do not retranslate
            // previously emitted content; wait for a new stable suffix.
            if (isFinal) _consumed = 0;
            return;
        }

        int length = text.Length - _consumed;
        int available = isFinal ? length : Math.Max(0, length - UnstableTail);
        while (available >= MinimumSegment || (isFinal && length > 0))
        {
            int limit = Math.Min(isFinal ? length : available, MaximumSegment);
            int end = _consumed + limit;
            if (limit < length)
            {
                // Split only at a word boundary. Do not cut identifiers in half.
                int boundary = text.LastIndexOf(' ', end - 1, limit);
                if (boundary >= _consumed + Math.Min(MinimumSegment, limit))
                    end = boundary + 1;
            }
            if (end <= _consumed) break;
            string segment = text[_consumed..end].Trim();
            if (segment.Length > 0) _ready.Enqueue(segment);
            _consumed = end;
            length = text.Length - _consumed;
            available = isFinal ? length : Math.Max(0, length - UnstableTail);
        }

        if (isFinal)
        {
            // ASR resets its hypothesis after endpoint; queued translations
            // stay in order while source indexing resets to the next utterance.
            _consumed = 0;
        }
    }

    /// <summary>Flushes at most maxInFlight pending segments, without dropping
    /// any segments when the downstream translation queue is congested.</summary>
    public void Drain(Func<string, bool> tryEnqueue, int maxSegments = 1)
    {
        for (int i = 0; i < maxSegments && _ready.TryPeek(out string? text); i++)
        {
            if (!tryEnqueue(text)) break;
            _ready.Dequeue();
        }
    }

    public void Clear()
    {
        _ready.Clear();
        _consumed = 0;
    }
}
