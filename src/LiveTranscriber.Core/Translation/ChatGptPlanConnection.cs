using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveTranscriber.Core.Translation;

/// <summary>
/// OpenAI's public, open-source Sign in with ChatGPT OAuth client.
/// Credentials are held only in process memory. Nothing is written to logs,
/// diagnostics, audio storage, GitHub, or token files.
/// </summary>
public sealed class ChatGptPlanConnection : IAsyncDisposable
{
    internal const string AuthBase = "https://auth.openai.com";
    internal const string Resource = "https://api.openai.com/v1";
    private static readonly Uri AuthorizationUri = new(AuthBase + "/api/accounts/authorize");
    private static readonly Uri TokenUri = new(AuthBase + "/api/accounts/oauth/token");
    private static readonly Uri DiscoveryUri = new(AuthBase + "/.well-known/openid-configuration");
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Func<Uri, Task> _openBrowser;
    private string? _accessToken;
    private string? _refreshToken;
    private string? _clientId;
    private string? _subject;
    private DateTimeOffset _expiresAt;
    private bool _disposed;

    public bool IsConnected => _accessToken is not null && _clientId is not null;
    public string Status => IsConnected ? "ChatGPT plan authorized (session only)" : "Not connected";

    public ChatGptPlanConnection(
        HttpClient? httpClient = null, Func<Uri, Task>? browserOpener = null)
    {
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
        _openBrowser = browserOpener ?? (uri =>
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            return Task.CompletedTask;
        });
    }

    private static string RandomUrlSafe(int byteCount = 32) =>
        Base64Url(RandomNumberGenerator.GetBytes(byteCount));

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] DecodeBase64Url(string input)
    {
        string value = input.Replace('-', '+').Replace('_', '/');
        value = value.PadRight((value.Length + 3) / 4 * 4, '=');
        return Convert.FromBase64String(value);
    }

    internal static string PkceChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    internal static string BuildAuthorizeUrl(
        Uri callback, string hostId, string state, string nonce, string pkceChallenge)
    {
        var options = new Dictionary<string, string>
        {
            ["client_id"] = "dynamic_agent_client",
            ["agent_name_hint"] = "LiveTranscriber",
            ["ext_agent_host_id"] = hostId,
            ["response_type"] = "code",
            ["redirect_uri"] = callback.ToString(),
            ["scope"] = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct",
            ["resource"] = Resource,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = pkceChallenge
        };
        return AuthorizationUri + "?" + string.Join("&", options.Select(
            x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
    }

    /// <summary>Requires an actual browser approval; validates the signed OIDC ID token.</summary>
    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Only public host ID persisted locally, NEVER session or refresh tokens.
        string hostId = GetOrCreateHostId();
        string verifier = RandomUrlSafe(32);
        string state = RandomUrlSafe();
        string nonce = RandomUrlSafe();
        using var listener = CreateLoopbackListener(out Uri callback);
        string authorizationUrl = BuildAuthorizeUrl(callback, hostId, state, nonce,
            PkceChallenge(verifier));
        await _openBrowser(new Uri(authorizationUrl)).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        HttpListenerContext context = await listener.GetContextAsync().WaitAsync(timeout.Token)
            .ConfigureAwait(false);
        bool validPath = context.Request.Url?.AbsolutePath == "/auth/callback";
        string returnedState = context.Request.QueryString["state"] ?? "";
        bool stateMatches = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(state), Encoding.ASCII.GetBytes(returnedState));
        string? code = context.Request.QueryString["code"];
        string? issuedClientId = context.Request.QueryString["client_id"];
        bool accepted = validPath && stateMatches &&
            context.Request.QueryString["error"] is null &&
            !string.IsNullOrWhiteSpace(code) &&
            issuedClientId?.StartsWith("oaiapp_", StringComparison.Ordinal) == true;

        byte[] page = Encoding.UTF8.GetBytes(accepted
            ? "<html><body>ChatGPT sign-in received. Return to LiveTranscriber.</body></html>"
            : "<html><body>ChatGPT sign-in failed. Return to LiveTranscriber.</body></html>");
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.StatusCode = accepted ? 200 : 400;
        context.Response.ContentLength64 = page.Length;
        await context.Response.OutputStream.WriteAsync(page, timeout.Token).ConfigureAwait(false);
        context.Response.Close();
        if (!accepted) throw new InvalidOperationException("ChatGPT authorization was not completed.");

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = issuedClientId!,
                ["code"] = code!,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = callback.ToString(),
                ["resource"] = Resource
            })
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("ChatGPT sign-in token exchange failed. Try connecting again.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(
            cancellationToken).ConfigureAwait(false));
        JsonElement token = document.RootElement;
        string idToken = token.GetProperty("id_token").GetString() ?? "";
        string subject = await VerifyIdTokenAsync(idToken, issuedClientId!, nonce,
            cancellationToken).ConfigureAwait(false);
        string scopes = token.GetProperty("scope").GetString() ?? "";
        if (!HasPlanPermission(scopes))
            throw new InvalidOperationException(
                "ChatGPT sign-in succeeded, but plan usage permission was not granted.");

        string newAccess = token.GetProperty("access_token").GetString() ?? "";
        if (newAccess.Length == 0) throw new InvalidOperationException("ChatGPT access token is missing.");
        // Commit the new session only after signature, nonce, scopes and issuer validate.
        _accessToken = newAccess;
        _refreshToken = token.TryGetProperty("refresh_token", out var refresh)
            ? refresh.GetString() : null;
        _clientId = issuedClientId;
        _subject = subject;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(
            token.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600);
    }

    internal static bool HasPlanPermission(string scope) =>
        scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("resource.invoke") &&
        scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("chatgpt.tokens.use.direct");

    private static string GetOrCreateHostId()
    {
        string folder = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "LiveTranscriber");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "chatgpt-agent-host-id.txt");
        if (File.Exists(file))
        {
            string saved = File.ReadAllText(file).Trim();
            if (Guid.TryParse(saved.TrimStart('\uFEFF').Replace("urn:uuid:", "", StringComparison.Ordinal), out _))
                return saved.StartsWith("urn:uuid:", StringComparison.Ordinal)
                    ? saved : "urn:uuid:" + saved;
        }
        string host = "urn:uuid:" + Guid.NewGuid().ToString();
        File.WriteAllText(file, host, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return host;
    }

    private static HttpListener CreateLoopbackListener(out Uri callback)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/auth/");
            try
            {
                listener.Start();
                callback = new Uri($"http://127.0.0.1:{port}/auth/callback");
                return listener;
            }
            catch (HttpListenerException) { listener.Close(); }
        }
        throw new InvalidOperationException("Could not open the local ChatGPT sign-in callback.");
    }

    private async Task<string> VerifyIdTokenAsync(
        string token, string clientId, string nonce, CancellationToken cancellationToken)
    {
        string[] parts = token.Split('.');
        if (parts.Length != 3) throw new InvalidOperationException("Invalid ChatGPT ID token.");
        using var header = JsonDocument.Parse(DecodeBase64Url(parts[0]));
        using var payload = JsonDocument.Parse(DecodeBase64Url(parts[1]));
        if (header.RootElement.GetProperty("alg").GetString() != "RS256")
            throw new InvalidOperationException("Unexpected ChatGPT ID-token algorithm.");
        string kid = header.RootElement.GetProperty("kid").GetString() ?? "";

        using var discovery = await _http.GetAsync(DiscoveryUri, cancellationToken).ConfigureAwait(false);
        discovery.EnsureSuccessStatusCode();
        using var configuration = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync(
            cancellationToken).ConfigureAwait(false));
        string issuer = configuration.RootElement.GetProperty("issuer").GetString() ?? "";
        string jwks = configuration.RootElement.GetProperty("jwks_uri").GetString() ?? "";
        if (issuer != AuthBase || !Uri.TryCreate(jwks, UriKind.Absolute, out var jwksUri) ||
            jwksUri.Scheme != "https" || jwksUri.Host != "auth.openai.com")
            throw new InvalidOperationException("Unexpected ChatGPT OIDC metadata.");

        using var keysResponse = await _http.GetAsync(jwksUri, cancellationToken).ConfigureAwait(false);
        keysResponse.EnsureSuccessStatusCode();
        using var keys = JsonDocument.Parse(await keysResponse.Content.ReadAsStringAsync(
            cancellationToken).ConfigureAwait(false));
        JsonElement? matching = null;
        foreach (var key in keys.RootElement.GetProperty("keys").EnumerateArray())
        {
            if (key.GetProperty("kid").GetString() == kid &&
                key.GetProperty("kty").GetString() == "RSA")
            {
                matching = key;
                break;
            }
        }
        if (matching is null) throw new InvalidOperationException("ChatGPT ID-token signing key not found.");
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = DecodeBase64Url(matching.Value.GetProperty("n").GetString()!),
            Exponent = DecodeBase64Url(matching.Value.GetProperty("e").GetString()!)
        });
        if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]),
            DecodeBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidOperationException("ChatGPT ID-token signature verification failed.");

        var claims = payload.RootElement;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string tokenIssuer = claims.GetProperty("iss").GetString() ?? "";
        var audiences = claims.GetProperty("aud");
        bool audienceMatches = audiences.ValueKind == JsonValueKind.String
            ? audiences.GetString() == clientId
            : audiences.ValueKind == JsonValueKind.Array && audiences.EnumerateArray()
                .Any(x => x.GetString() == clientId);
        if (tokenIssuer != AuthBase || !audienceMatches ||
            claims.GetProperty("nonce").GetString() != nonce ||
            claims.GetProperty("exp").GetInt64() <= now ||
            (claims.TryGetProperty("nbf", out var nbf) && nbf.GetInt64() > now + 60))
            throw new InvalidOperationException("ChatGPT ID-token validation failed.");

        string subject = claims.GetProperty("sub").GetString() ?? "";
        if (subject.Length == 0) throw new InvalidOperationException("ChatGPT account ID missing.");
        return subject;
    }

    public void Disconnect()
    {
        _accessToken = null;
        _refreshToken = null;
        _clientId = null;
        _subject = null;
        _expiresAt = default;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || !IsConnected)
            throw new InvalidOperationException("Connect ChatGPT first using the sign-in button.");
        if (DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-3)) return _accessToken!;
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-3)) return _accessToken!;
            if (_refreshToken is null)
                throw new InvalidOperationException("ChatGPT authorization expired. Sign in again.");
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = _clientId!,
                    ["refresh_token"] = _refreshToken,
                    ["resource"] = Resource
                })
            };
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("ChatGPT authorization expired. Sign in again.");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(
                cancellationToken).ConfigureAwait(false));
            string scopes = body.RootElement.GetProperty("scope").GetString() ?? "";
            if (!HasPlanPermission(scopes))
                throw new InvalidOperationException("ChatGPT plan permission was revoked.");
            // Rotate refresh and access together; no storage on disk.
            string nextAccess = body.RootElement.GetProperty("access_token").GetString() ?? "";
            string nextRefresh = body.RootElement.GetProperty("refresh_token").GetString() ?? "";
            if (nextAccess.Length == 0 || nextRefresh.Length == 0)
                throw new InvalidOperationException("ChatGPT session could not be renewed.");
            _accessToken = nextAccess;
            _refreshToken = nextRefresh;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(
                body.RootElement.GetProperty("expires_in").GetInt32());
            return nextAccess;
        }
        finally { _refreshLock.Release(); }
    }

    public async Task<IReadOnlyList<ChatGptModel>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        string token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, Resource + "/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("ChatGPT model list is unavailable for this account.");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(
            cancellationToken).ConfigureAwait(false));
        var models = new List<ChatGptModel>();
        foreach (JsonElement item in body.RootElement.GetProperty("models").EnumerateArray())
        {
            if (item.TryGetProperty("visibility", out var visibility) &&
                visibility.GetString() != "list") continue;
            string slug = item.GetProperty("slug").GetString() ?? "";
            string name = item.TryGetProperty("display_name", out var label)
                ? label.GetString() ?? slug : slug;
            if (!string.IsNullOrWhiteSpace(slug)) models.Add(new ChatGptModel(slug, name));
        }
        return models;
    }

    public HttpClient Http => _http;

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        Disconnect();
        _refreshLock.Dispose();
        if (_ownsHttp) _http.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed record ChatGptModel(string Slug, string DisplayName)
{
    public override string ToString() => DisplayName;
}
