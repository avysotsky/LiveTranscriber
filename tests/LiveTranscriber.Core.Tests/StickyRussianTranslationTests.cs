using LiveTranscriber.Core.Translation;
using Xunit;

namespace LiveTranscriber.Core.Tests;

public sealed class StickyRussianTranslationTests
{
    [Fact]
    public void RussianNeverBlanksWhenEnglishEndsOrNextGptAnswerStarts()
    {
        var state = new RussianTranscriptBuffer();
        Assert.True(state.UpdatePreview("Это уже видимый русский перевод"));
        string visible = state.Text;
        Assert.Contains("уже видимый", visible);

        // The ASR final endpoint and failed request do not call Clear:
        // an empty/stale callback must never erase already visible text.
        Assert.False(state.UpdatePreview(""));
        Assert.False(state.UpdatePreview("Э"));
        Assert.Equal(visible, state.Text);

        // Once a useful updated translation arrives, replace provisionally.
        Assert.True(state.UpdatePreview("Это уже обновленный перевод продолжающейся речи"));
        Assert.Contains("обновленный перевод", state.Text);
        state.Commit("Это окончательно переведённый фрагмент.");
        Assert.Contains("окончательно переведённый", state.Text);
        Assert.DoesNotContain("обновленный перевод", state.Text);
        Assert.NotEmpty(state.Text);

        // After an error, translated history stays on screen.
        Assert.False(state.UpdatePreview(null));
        Assert.Contains("окончательно переведённый", state.Text);
        Assert.True(state.UpdatePreview("Следующий русский фрагмент"));
        Assert.Contains("окончательно переведённый", state.Text);
        Assert.Contains("Следующий", state.Text);
        state.Commit("Следующий русский фрагмент");
        Assert.Contains("окончательно переведённый", state.Text);
        Assert.Contains("Следующий", state.Text);

        state.Clear();
        Assert.Equal("", state.Text); // Only explicit user Clear removes it.
    }

    [Fact]
    public void EnglishContinuousSpeechIsSplitAndTranslatedBeforeEndOfUtterance()
    {
        var chunker = new IncrementalEnglishChunker();
        var submitted = new List<string>();
        const string source = "Could you please describe dependency injection in ASP.NET Core " +
            "and explain why scoped lifetimes are useful for services " +
            "that access the database in a typical Web API application.";

        chunker.Observe(source[..84], isFinal: false);
        chunker.Drain(text => { submitted.Add(text); return true; });
        Assert.NotEmpty(submitted); // The speaker did not pause, yet a chunk is ready.

        chunker.Observe(source[..125], isFinal: false);
        chunker.Drain(text => { submitted.Add(text); return true; });
        chunker.Observe(source, isFinal: true);
        chunker.Drain(text => { submitted.Add(text); return true; }, maxSegments: 50);

        Assert.Equal(source, string.Join(" ", submitted));
        Assert.All(submitted, text => Assert.InRange(text.Length, 1, 105));
        Assert.Equal(0, chunker.PendingSegments);
    }

    [Fact]
    public void BackpressureKeepsEnglishSegmentsRatherThanDroppingThem()
    {
        var chunker = new IncrementalEnglishChunker();
        var sent = new List<string>();
        const string source = "Could you explain the difference between IEnumerable " +
            "and IQueryable and how Entity Framework translates queries?";
        chunker.Observe(source, isFinal: true);
        int before = chunker.PendingSegments;
        Assert.True(before > 0);
        chunker.Drain(_ => false, maxSegments: 5);
        Assert.Equal(before, chunker.PendingSegments);
        chunker.Drain(text => { sent.Add(text); return true; }, maxSegments: 50);
        Assert.Equal(source, string.Join(" ", sent));
        Assert.Equal(0, chunker.PendingSegments);
    }

    [Fact]
    public void TwoSeparateAsrFinalPhrasesDoNotTranslateEarlierEnglishAgain()
    {
        var chunker = new IncrementalEnglishChunker();
        var sent = new List<string>();
        chunker.Observe("Can you explain Dependency Injection?", isFinal: true);
        chunker.Drain(text => { sent.Add(text); return true; }, maxSegments: 10);
        chunker.Observe("What is a scoped service?", isFinal: true);
        chunker.Drain(text => { sent.Add(text); return true; }, maxSegments: 10);
        Assert.Equal(new[] { "Can you explain Dependency Injection?",
            "What is a scoped service?" }, sent);
    }
}
