namespace LiveTranscriber.Core.Translation;

/// <summary>
/// An optional low-latency text translator that emits provisional cumulative
/// Russian text while a request is still being processed. Only the returned
/// Task result is final. Nothing provisional is saved as a final transcript.
/// </summary>
public interface IStreamingTextTranslator : ITextTranslator
{
    Task<string> TranslateToRussianStreamingAsync(
        string englishText, Action<string> onPartial,
        CancellationToken cancellationToken);
}
