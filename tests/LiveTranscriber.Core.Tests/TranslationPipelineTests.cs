using System.Collections.Concurrent;
using LiveTranscriber.Core.Translation;
using Xunit;

namespace LiveTranscriber.Core.Tests;

public sealed class TranslationPipelineTests
{
    [Fact]
    public async Task FinalPhrasesAreTranslatedInOriginalOrder()
    {
        var backend = new EchoTranslator();
        await using var sut = new TranslationPipeline(backend, TimeSpan.Zero);
        var results = new ConcurrentQueue<string>();
        sut.Translated += (text, _) => results.Enqueue(text);
        Assert.True(sut.TryEnqueueFinal("First sentence."));
        Assert.True(sut.TryEnqueueFinal("Second sentence."));
        Assert.True(sut.TryEnqueueFinal("Third sentence."));
        await sut.CompleteAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("First sentence.\nSecond sentence.\nThird sentence.",
            string.Join("\n", results));
        Assert.Equal(0, sut.PendingPhrases);
    }

    [Fact]
    public async Task InterimHypothesisTranslatesWithoutAnyFinalEvent()
    {
        var backend = new EchoTranslator();
        await using var sut = new TranslationPipeline(backend, TimeSpan.Zero);
        var text = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.PreviewTranslated += (russian, _) => text.TrySetResult(russian);
        Assert.True(sut.TryEnqueuePreview("Can you explain dependency injection?"));
        Assert.Equal("Can you explain dependency injection?",
            await text.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await sut.CompleteAsync(TimeSpan.FromSeconds(5));
        var metrics = sut.GetMetrics();
        Assert.Equal(0, metrics.FinalPhrasesQueued);
        Assert.Equal(1, metrics.PreviewPhrasesQueued);
        Assert.Equal(1, metrics.ApiRequestsSucceeded);
    }

    [Fact]
    public async Task FinalUtteranceInvalidatesRunningInterimResult()
    {
        var backend = new BlockingFirstTranslator();
        await using var sut = new TranslationPipeline(backend, TimeSpan.Zero);
        var previews = new ConcurrentQueue<string>();
        var finals = new ConcurrentQueue<string>();
        sut.PreviewTranslated += (text, _) => previews.Enqueue(text);
        sut.Translated += (text, _) => finals.Enqueue(text);
        Assert.True(sut.TryEnqueuePreview("partial speech"));
        await backend.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(sut.TryEnqueueFinal("final complete sentence"));
        backend.ReleaseFirst.TrySetResult();
        await sut.CompleteAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(previews);
        Assert.Equal(new[] { "final complete sentence" }, finals.ToArray());
        Assert.Equal(2, sut.GetMetrics().ApiRequestsSucceeded);
    }

    [Fact]
    public async Task SecondInterimSupersedesFirstAndOnlyLatestAppears()
    {
        var backend = new BlockingFirstTranslator();
        await using var sut = new TranslationPipeline(backend, TimeSpan.Zero);
        var previews = new ConcurrentQueue<string>();
        sut.PreviewTranslated += (text, _) => previews.Enqueue(text);
        Assert.True(sut.TryEnqueuePreview("old interim"));
        await backend.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(sut.TryEnqueuePreview("current interim"));
        backend.ReleaseFirst.TrySetResult();
        await sut.CompleteAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "current interim" }, previews.ToArray());
    }

    [Fact]
    public async Task NonFinalTextIsNotQueuedByEmptyInput()
    {
        var backend = new EchoTranslator();
        await using var sut = new TranslationPipeline(backend, TimeSpan.Zero);
        Assert.True(sut.TryEnqueueFinal("    "));
        await sut.CompleteAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(backend.Requests);
    }

    [Fact]
    public async Task ClearPreventsAnAlreadyRunningTranslationFromAppearing()
    {
        var backend = new BlockingFirstTranslator();
        await using var sut = new TranslationPipeline(backend, TimeSpan.Zero);
        var translations = new ConcurrentQueue<string>();
        sut.Translated += (text, _) => translations.Enqueue(text);
        Assert.True(sut.TryEnqueueFinal("old text"));
        await backend.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sut.ClearPending();
        Assert.True(sut.TryEnqueueFinal("new text"));
        backend.ReleaseFirst.TrySetResult();
        await sut.CompleteAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "new text" }, translations.ToArray());
    }

    [Fact]
    public async Task ProducerNeverBlocksAndOverflowIsExplicit()
    {
        var backend = new BlockingFirstTranslator();
        await using var sut = new TranslationPipeline(backend, TimeSpan.Zero);
        Assert.True(sut.TryEnqueueFinal("active"));
        await backend.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 64; i++)
            Assert.True(sut.TryEnqueueFinal("pending " + i));
        string? error = null;
        sut.Error += message => error = message;
        Assert.False(sut.TryEnqueueFinal("overflow"));
        Assert.NotNull(error);
        Assert.Contains("full", error);
        backend.ReleaseFirst.TrySetResult();
        await sut.CompleteAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class EchoTranslator : ITextTranslator
    {
        public ConcurrentQueue<string> Requests { get; } = new();
        public Task<string> TranslateToRussianAsync(string text, CancellationToken ct)
        {
            Requests.Enqueue(text);
            return Task.FromResult(text);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingFirstTranslator : ITextTranslator
    {
        private int _calls;
        public TaskCompletionSource FirstEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> TranslateToRussianAsync(string text, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstEntered.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(ct);
            }
            return text;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
