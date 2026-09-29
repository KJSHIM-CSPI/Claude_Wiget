using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ClaudeUsageWidget.Core;

public sealed class AuthenticationRequiredException : Exception
{
    public AuthenticationRequiredException() : base("사용 가능한 인증이 없습니다. ‘연결’을 눌러 브라우저에서 로그인해 주세요.") { }
}

public sealed class OAuthUsageClient : IDisposable
{
    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    public const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private readonly HttpClient _http;
    private readonly IOAuthTokenStore _store;
    private readonly Func<OAuthCredentials?> _readCode;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _rejected = [];
    private volatile bool _disconnected;
    public string Source { get; private set; } = "";
    public DateTimeOffset RetryUntil { get; private set; }

    public OAuthUsageClient(IOAuthTokenStore store, Func<OAuthCredentials?>? readCode = null, HttpMessageHandler? handler = null, bool disconnected = false)
    {
        _disconnected = disconnected;
        _store = store; _readCode = readCode ?? (() => ClaudeCodeCredentials.Read());
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
    }

    public async Task<string> FetchAsync(CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            CheckConnected();
            CheckCooldown();
            var code = _readCode();
            if (code is { IsExpired: false } && !_rejected.Contains(code.AccessToken))
            {
                try { var json = await FetchTokenAsync(code.AccessToken, cancellation); Source = "Claude Code"; return json; }
                catch (AuthenticationRequiredException) { _rejected.Add(code.AccessToken); }
            }
            var own = _store.Load();
            if (own is null || string.IsNullOrWhiteSpace(own.AccessToken) || _rejected.Contains(own.AccessToken)) throw new AuthenticationRequiredException();
            if (own.IsExpired) own = await RefreshAsync(own, cancellation);
            try { var json = await FetchTokenAsync(own.AccessToken, cancellation); Source = "브라우저"; return json; }
            catch (AuthenticationRequiredException)
            {
                own = await RefreshAsync(own, cancellation);
                try { var json = await FetchTokenAsync(own.AccessToken, cancellation); Source = "브라우저"; return json; }
                catch (AuthenticationRequiredException) { _rejected.Add(own.AccessToken); throw; }
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<string> FetchTokenAsync(string token, CancellationToken cancellation)
    {
        CheckConnected();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.Add("anthropic-version", "2023-06-01");
        using var response = await _http.SendAsync(request, cancellation);
        CheckResponse(response);
        return await response.Content.ReadAsStringAsync(cancellation);
    }

    public async Task CompleteLoginAsync(string code, string verifier, string redirect, string state, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            CheckConnected();
            var token = await ExchangeAsync(new() { ["grant_type"] = "authorization_code", ["client_id"] = ClientId,
                ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["state"] = state }, cancellation);
            cancellation.ThrowIfCancellationRequested();
            CheckConnected();
            _store.Save(token);
        }
        finally { _gate.Release(); }
    }

    private async Task<OAuthCredentials> RefreshAsync(OAuthCredentials old, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(old.RefreshToken)) { _rejected.Add(old.AccessToken); throw new AuthenticationRequiredException(); }
        OAuthCredentials token;
        try { token = await ExchangeAsync(new() { ["grant_type"] = "refresh_token", ["client_id"] = ClientId, ["refresh_token"] = old.RefreshToken }, cancellation); }
        catch (AuthenticationRequiredException) { _rejected.Add(old.AccessToken); throw; }
        token = new() { AccessToken = token.AccessToken, RefreshToken = token.RefreshToken ?? old.RefreshToken, ExpiresAt = token.ExpiresAt };
        CheckConnected();
        _store.Save(token);
        return token;
    }

    private async Task<OAuthCredentials> ExchangeAsync(Dictionary<string, string> body, CancellationToken cancellation)
    {
        CheckConnected();
        CheckCooldown();
        using var response = await _http.PostAsJsonAsync(TokenUrl, body, cancellation);
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new AuthenticationRequiredException();
        CheckResponse(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var root = json.RootElement;
        if (!root.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(access.GetString()))
            throw new AuthenticationRequiredException();
        return new() { AccessToken = access.GetString()!,
            RefreshToken = root.TryGetProperty("refresh_token", out var refresh) && refresh.ValueKind == JsonValueKind.String ? refresh.GetString() : null,
            ExpiresAt = root.TryGetProperty("expires_in", out var expiry) && expiry.TryGetInt32(out var seconds) && seconds > 0
                ? DateTimeOffset.UtcNow.AddSeconds(seconds) : DateTimeOffset.UtcNow.AddHours(1) };
    }

    private void CheckCooldown()
    {
        if (RetryUntil > DateTimeOffset.UtcNow)
            throw new UsageConnectionException("요청 제한이 끝날 때까지 기다려 주세요.", 429, (int)Math.Ceiling((RetryUntil - DateTimeOffset.UtcNow).TotalSeconds));
    }

    public void EnableConnection() => _disconnected = false;
    public void SuspendConnection() => _disconnected = true;

    public async Task DisconnectAsync()
    {
        // Block queued work immediately; drain any token refresh before deleting its result.
        _disconnected = true;
        _http.CancelPendingRequests();
        await _gate.WaitAsync();
        try
        {
            Source = "";
            _rejected.Clear();
            _store.Delete();
        }
        finally { _gate.Release(); }
    }

    private void CheckConnected()
    {
        if (_disconnected) throw new AuthenticationRequiredException();
    }

    private void CheckResponse(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new AuthenticationRequiredException();
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter;
            var seconds = retry?.Delta?.TotalSeconds ?? (retry?.Date - DateTimeOffset.UtcNow)?.TotalSeconds ?? 60;
            RetryUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(Math.Ceiling(seconds), 1, 86400));
            throw new UsageConnectionException("요청이 많아 잠시 대기합니다. 자동으로 다시 조회합니다.", 429, (int)Math.Clamp(Math.Ceiling(seconds), 1, 86400));
        }
        if (!response.IsSuccessStatusCode) throw new UsageConnectionException($"사용량 서버에 연결하지 못했습니다. (HTTP {(int)response.StatusCode})", (int)response.StatusCode);
    }

    public void Dispose() { _http.Dispose(); }
}
