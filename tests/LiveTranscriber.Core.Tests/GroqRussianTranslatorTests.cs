using System.Net;
using System.Text.Json;
using LiveTranscriber.Core.Translation;
using Xunit;

namespace LiveTranscriber.Core.Tests;

public sealed class GroqRussianTranslatorTests
{
    [Fact]
    public async Task SendsOnlyFinalTextWithAuthorizationAndReadsRussianResult()
    {
        var handler = new StubHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.groq.com/openai/v1/chat/completions", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("example-test-key", request.Headers.Authorization.Parameter);
            string json = await request.Content!.ReadAsStringAsync(ct);
            using var body = JsonDocument.Parse(json);
            Assert.Equal("openai/gpt-oss-20b", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("low", body.RootElement.GetProperty("reasoning_effort").GetString());
            Assert.False(body.RootElement.GetProperty("include_reasoning").GetBoolean());
            string source = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Assert.Contains("dependency injection", source);
            Assert.Contains("Russian", source);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"Внедрение зависимостей\"}}]}")
            };
        });
        using var client = new HttpClient(handler);
        await using var sut = new GroqRussianTranslator("example-test-key", client);
        string result = await sut.TranslateToRussianAsync("dependency injection", CancellationToken.None);
        Assert.Equal("Внедрение зависимостей", result);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task UnauthorizedErrorDoesNotLeakKeyOrSubmittedInterviewText()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("SECRET_KEY and private interview text")
            }));
        using var client = new HttpClient(handler);
        await using var sut = new GroqRussianTranslator("SECRET_KEY", client);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TranslateToRussianAsync("private interview text", CancellationToken.None));
        Assert.DoesNotContain("SECRET_KEY", ex.Message);
        Assert.DoesNotContain("private interview text", ex.Message);
    }

    [Fact]
    public void MissingKeyIsRejectedBeforeAnyRequest()
    {
        Assert.Throws<InvalidOperationException>(() => new GroqRussianTranslator(" "));
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return responder(request, cancellationToken);
        }
    }
}
