using LiveTranscriber.Core.Translation;
using Xunit;

namespace LiveTranscriber.Core.Tests;

public sealed class LocalOpusMtTranslatorTests
{
    [Fact]
    public void MissingOfflineModelFailsClearlyWithoutOpeningNetwork()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LocalOpusMtTranslator.ValidateFiles(
                Path.Combine(Path.GetTempPath(), "missing-lt-opus-model"),
                "missing-worker.py"));
        Assert.Contains("setup", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"type\":\"ready\"}", true)]
    [InlineData("{\"type\":\"error\",\"error\":\"no model\"}", false)]
    [InlineData("not-json", false)]
    [InlineData("{}", false)]
    public void WorkerHandshakeIsStrictAndPrivate(string line, bool ready)
    {
        Assert.Equal(ready, LocalOpusMtTranslator.IsReadyMessage(line));
    }
}
