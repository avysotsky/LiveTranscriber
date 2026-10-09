using LiveTranscriber.Core.Translation;
using Xunit;

namespace LiveTranscriber.Core.Tests;

public sealed class ReusableTranslatorHostTests
{
    [Fact]
    public async Task SessionProxyDoesNotStartModelUntilTranslationRequested()
    {
        var loads = 0;
        var backend = new FakeTranslator();
        await using var host = new ReusableTranslatorHost(_ =>
        {
            Interlocked.Increment(ref loads);
            return Task.FromResult<ITextTranslator>(backend);
        });

        await using (var proxy = host.CreateSessionTranslator())
        {
            Assert.False(host.IsReady);
            Assert.Equal(0, loads);
            Assert.Equal("RU:hello", await proxy.TranslateToRussianAsync("hello", CancellationToken.None));
        }
        Assert.True(host.IsReady);
        Assert.Equal(1, loads);
        Assert.Equal(0, backend.DisposeCount); // Stop does not unload model.

        await using (var second = host.CreateSessionTranslator())
            Assert.Equal("RU:world", await second.TranslateToRussianAsync("world", CancellationToken.None));
        Assert.Equal(1, loads);
        Assert.Equal(0, backend.DisposeCount);

        await host.DisposeAsync();
        Assert.Equal(1, backend.DisposeCount); // OnClosed unloads exactly once.
    }

    [Fact]
    public async Task ModelStartupDoesNotBlockSessionConstructionOrEnglishAsr()
    {
        var loading = new TaskCompletionSource<ITextTranslator>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeTranslator();
        await using var host = new ReusableTranslatorHost(_ => loading.Task);
        await using var session = host.CreateSessionTranslator();

        Task<string> pending = session.TranslateToRussianAsync("english", CancellationToken.None);
        Assert.False(pending.IsCompleted);
        Assert.False(host.IsReady);
        // WPF can already start its ASR with the lightweight session proxy.
        loading.TrySetResult(backend);
        Assert.Equal("RU:english", await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task PreparedTestTranslatorIsReusedForLiveSession()
    {
        int starts = 0;
        var backend = new FakeTranslator();
        await using var host = new ReusableTranslatorHost(_ =>
        {
            Interlocked.Increment(ref starts);
            return Task.FromResult<ITextTranslator>(backend);
        });
        Assert.Same(backend, await host.PrepareAsync()); // Test translator.
        await using var session = host.CreateSessionTranslator();
        Assert.Equal("RU:question", await session.TranslateToRussianAsync("question", CancellationToken.None));
        Assert.Equal(1, starts);
    }

    private sealed class FakeTranslator : ITextTranslator
    {
        public int DisposeCount { get; private set; }
        public Task<string> TranslateToRussianAsync(string englishText, CancellationToken cancellationToken) =>
            Task.FromResult("RU:" + englishText);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
