using System.Text;

namespace LiveTranscriber.Core.Translation;

/// <summary>
/// Presentation state independent from ASR endpoints and provider requests.
/// Completed Russian chunks persist, while the current provisional fragment
/// remains visible until a replacement or a successful commit is available.
/// Only the user's explicit Clear action removes the displayed history.
/// </summary>
public sealed class RussianTranscriptBuffer
{
    private readonly StringBuilder _committed = new();
    private string _preview = "";

    public string Text => _committed.ToString() +
        (string.IsNullOrWhiteSpace(_preview) ? "" : _preview + " …");
    public string CommittedText => _committed.ToString();
    public string PreviewText => _preview;

    public bool UpdatePreview(string? partial)
    {
        string next = partial?.Trim() ?? "";
        if (next.Length == 0) return false;
        // Keep a useful previous translation while the next request has
        // only emitted its first letter(s), instead of flashing a blank field.
        int minReplacement = Math.Min(24, _preview.Length / 2);
        if (_preview.Length > 0 && next.Length < minReplacement) return false;
        if (next == _preview) return false;
        _preview = next;
        return true;
    }

    public void Commit(string? completed)
    {
        if (string.IsNullOrWhiteSpace(completed)) return;
        _committed.AppendLine(completed.Trim());
        // Atomic replacement: the confirmed text is appended BEFORE the
        // provisional slot disappears, with no intervening empty render.
        _preview = "";
    }

    // Intentionally NO reset on English ASR endpoint or provider failure:
    // both occur before the next successful Russian translation arrives.

    public void Clear()
    {
        _committed.Clear();
        _preview = "";
    }
}
