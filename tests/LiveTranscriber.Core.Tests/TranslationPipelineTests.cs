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
        var latest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.PreviewTranslated += (text, _) =>
        {
            previews.Enqueue(text);
            latest.TrySetResult(text);
        };
        Assert.True(sut.TryEnqueuePreview("old interim"));
        await backend.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(sut.TryEnqueuePreview("current interim"));
        backend.ReleaseFirst.TrySetResult();
        // Preview results are intentionally discarded on Stop; observe the
        // latest interim while the pipeline is still running.
        Assert.Equal("current interim",
            await latest.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await sut.CompleteAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "current interim" }, previews.ToArray());
    }

    [Fact]
    public async Task StreamedFinalIsShownProvisionallyBeforeFinalIsCommitted()
    {
        var backend = new ControlledStreamingTranslator();
        await using var sut = new TranslationPipeline(backend, TimeSpan.Zero);
        var interim = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var final = new ConcurrentQueue<string>();
        sut.PreviewTranslated += (text, _) => interim.TrySetResult(text);
        sut.Translated += (text, _) => final.Enqueue(text);
        Assert.True(sut.TryEnqueueFinal("Could you explain ASP.NET?"));
        await backend.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        backend.PublishPartial("Можете объяснить");
        Assert.Equal("Можете объяснить", await interim.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(final); // The translation is not final before response.completed.
        backend.Complete.TrySetResult("Можете объяснить ASP.NET?");
        await sut.CompleteAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "Можете объяснить ASP.NET?" }, final.ToArray());
    }

    [Fact]
    public async Task FailedStreamPreservesLastVisibleTextAndDoesNotAppendFinal()
    {
        var backend = new ControlledStreamingTranslator();
        await using var sut = new TranslationPipeline(backend, TimeSpan.Zero);
        var visible = new ConcurrentQueue<string>();
        var failures = new ConcurrentQueue<string>();
        sut.PreviewTranslated += (text, _) => visible.Enqueue(text);
        sut.Error += text => failures.Enqueue(text);
        Assert.True(sut.TryEnqueueFinal("Final English sentence"));
        await backend.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        backend.PublishPartial("Незаконченный перевод");
        backend.Fail.TrySetResult();
        await sut.CompleteAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "Незаконченный перевод" }, visible.ToArray());
        Assert.NotEmpty(failures);
        Assert.Equal(0, sut.GetMetrics().ApiRequestsSucceeded);
        Assert.Equal(1, sut.GetMetrics().ApiRequestsFailed);
    }

    [Fact]
    public async Task ClearingSessionHidesInFlightStreamedResult()
    {
        var backend = new ControlledStreamingTranslator();
        await using var sut = new TranslationPipeline(backend, TimeSpan.Zero);
        var visible = new ConcurrentQueue<string>();
        sut.PreviewTranslated += (text, _) => visible.Enqueue(text);
        Assert.True(sut.TryEnqueueFinal("old source"));
        await backend.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sut.ClearPending();
        backend.PublishPartial("old Russian");
        backend.Complete.TrySetResult("old Russian");
        await sut.CompleteAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(visible);
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

    private sealed class ControlledStreamingTranslator : IStreamingTextTranslator
    {
        private Action<string>? _sink;
        public TaskCompletionSource FirstEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Complete { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Fail { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void PublishPartial(string text) => _sink?.Invoke(text);

        public async Task<string> TranslateToRussianStreamingAsync(
            string english, Action<string> onPartial, CancellationToken token)
        {
            _sink = onPartial;
            FirstEntered.TrySetResult();
            var done = await Task.WhenAny(Complete.Task, Fail.Task).WaitAsync(token);
            if (done == Fail.Task) throw new InvalidOperationException("Mock stream interrupted");
            return await Complete.Task.WaitAsync(token);
        }

        public Task<string> TranslateToRussianAsync(string text, CancellationToken token) =>
            TranslateToRussianStreamingAsync(text, _ => { }, token);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
