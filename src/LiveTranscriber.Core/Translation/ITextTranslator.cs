namespace LiveTranscriber.Core.Translation;

/// <summary>Translates finalized English speech text; raw audio never enters this API.</summary>
public interface ITextTranslator : IAsyncDisposable
{
    Task<string> TranslateToRussianAsync(string englishText, CancellationToken cancellationToken);
}
