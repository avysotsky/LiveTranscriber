using System.Text;
using LiveTranscriber.Core.Translation;
using Xunit;

namespace LiveTranscriber.Core.Tests;

public sealed class ChatGptPlanTests
{
    [Fact]
    public void AuthorizationContainsRequiredDynamicClientPermissionsAndPkce()
    {
        var callback = new Uri("http://127.0.0.1:1455/auth/callback");
        string verifier = new string('X', 43);
        string challenge = ChatGptPlanConnection.PkceChallenge(verifier);
        string url = ChatGptPlanConnection.BuildAuthorizeUrl(
            callback, "urn:uuid:00000000-0000-0000-0000-000000000001",
            "random-state", "random-nonce", challenge);
        Assert.StartsWith("https://auth.openai.com/api/accounts/authorize?", url);
        Assert.Contains("dynamic_agent_client", url);
        Assert.Contains("chatgpt.tokens.use.direct", Uri.UnescapeDataString(url));
        Assert.Contains("resource.invoke", Uri.UnescapeDataString(url));
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains(Uri.EscapeDataString(callback.ToString()), url);
        Assert.DoesNotContain("client_secret", url);
        Assert.NotEqual(verifier, challenge);
    }

    [Fact]
    public void ReturningAuthorizationUsesPreviouslyIssuedClient()
    {
        string url = ChatGptPlanConnection.BuildAuthorizeUrl(
            new Uri("http://127.0.0.1:1455/auth/callback"),
            "urn:uuid:00000000-0000-0000-0000-000000000001",
            "state", "nonce", "challenge", "oaiapp_existing");
        Assert.Contains("client_id=oaiapp_existing", url);
        Assert.DoesNotContain("agent_name_hint", url);
        Assert.DoesNotContain("dynamic_agent_client", url);
    }

    [Theory]
    [InlineData("openid profile email offline_access resource.invoke chatgpt.tokens.use.direct", true)]
    [InlineData("openid profile email", false)]
    [InlineData("resource.invoke", false)]
    [InlineData("chatgpt.tokens.use.direct", false)]
    [InlineData("resource.invoke chatgpt.tokens.use.directx", false)]
    public void PlanUsageScopeMustBeExplicit(string scopes, bool authorized)
    {
        Assert.Equal(authorized, ChatGptPlanConnection.HasPlanPermission(scopes));
    }

    [Fact]
    public async Task CompletedStreamReturnsOnlyFullTranslation()
    {
        using var reader = new StringReader("""
            event: response.output_text.delta
            data: {"type":"response.output_text.delta","delta":"Внедрение "}
            
            event: response.output_text.delta
            data: {"type":"response.output_text.delta","delta":"зависимостей."}
            
            data: {"type":"response.completed","response":{"status":"completed"}}
            data: [DONE]
            """);
        string output = await ChatGptPlanTranslator.ReadCompletedTranslationAsync(
            reader, CancellationToken.None);
        Assert.Equal("Внедрение зависимостей.", output);
    }

    [Fact]
    public async Task StreamingTranslationPublishesRussianBeforeResponseCompleted()
    {
        var firstVisible = new List<string>();
        using var reader = new StringReader("""
            data: {"type":"response.output_text.delta","delta":"Перевод"}
            data: {"type":"response.output_text.delta","delta":" уже "}
            data: {"type":"response.output_text.delta","delta":"виден."}
            data: {"type":"response.completed","response":{"status":"completed"}}
            """);
        string result = await ChatGptPlanTranslator.ReadCompletedTranslationAsync(
            reader, CancellationToken.None, text => firstVisible.Add(text));
        Assert.NotEmpty(firstVisible);
        Assert.Equal("Перевод", firstVisible[0]);
        Assert.Equal("Перевод уже виден.", result);
    }

    [Fact]
    public async Task IncompleteStreamMayPublishProvisionalTextButCannotSucceed()
    {
        var previews = new List<string>();
        using var reader = new StringReader("""
            data: {"type":"response.output_text.delta","delta":"незаконченный"}
            data: {"type":"response.incomplete"}
            """);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ChatGptPlanTranslator.ReadCompletedTranslationAsync(
                reader, CancellationToken.None, text => previews.Add(text)));
        Assert.Equal(new[] { "незаконченный" }, previews);
    }

    [Fact]
    public async Task InterruptedStreamCannotBeMistakenForSuccess()
    {
        using var reader = new StringReader(
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"part\"}\n");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ChatGptPlanTranslator.ReadCompletedTranslationAsync(reader, CancellationToken.None));
        Assert.Contains("did not finish", ex.Message);
    }

    [Fact]
    public async Task PlanLimitDoesNotRevealRawProviderResponseOrTranscript()
    {
        using var reader = new StringReader("""
            data: {"type":"response.output_text.delta","delta":"private text"}
            data: {"type":"response.failed","response":{"error":{"code":"subscription_sharing_usage_limit_exceeded"}}}
            """);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ChatGptPlanTranslator.ReadCompletedTranslationAsync(reader, CancellationToken.None));
        Assert.DoesNotContain("private text", ex.Message);
        Assert.DoesNotContain("subscription_sharing_usage_limit_exceeded", ex.Message);
    }

    [Fact]
    public async Task IncompleteEventCannotAppearAsRussianTranslation()
    {
        using var reader = new StringReader(
            "data: {\"type\":\"response.incomplete\"}\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ChatGptPlanTranslator.ReadCompletedTranslationAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task SessionBeginsDisconnectedAndClearDoesNotPersistAuth()
    {
        await using var connection = new ChatGptPlanConnection();
        Assert.False(connection.IsConnected);
        Assert.Contains("Not connected", connection.Status);
        connection.Disconnect();
        Assert.False(connection.IsConnected);
    }
}
