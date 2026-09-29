using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeUsageWidget.Core;

public sealed class BrowserOAuthLogin : IDisposable
{
    private readonly TcpListener? _listener;
    private readonly string _verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
    private readonly string _state = Base64Url(RandomNumberGenerator.GetBytes(32));
    public string RedirectUri { get; }
    public Uri AuthorizeUri { get; }

    public BrowserOAuthLogin(bool manualCode = false)
    {
        if (manualCode) RedirectUri = "https://platform.claude.com/oauth/code/callback";
        else
        {
            _listener = new TcpListener(IPAddress.Loopback, 0); _listener.Start();
            RedirectUri = $"http://localhost:{((IPEndPoint)_listener.LocalEndpoint).Port}/callback";
        }
        var fields = new Dictionary<string, string> { ["client_id"] = OAuthUsageClient.ClientId, ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri, ["scope"] = "user:profile", ["state"] = _state,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(_verifier))), ["code_challenge_method"] = "S256" };
        if (manualCode) fields["code"] = "true";
        AuthorizeUri = new("https://claude.ai/oauth/authorize?" + string.Join("&", fields.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}")));
    }

    public async Task WaitAndCompleteAsync(OAuthUsageClient client, CancellationToken cancellation)
    {
        if (_listener is null) throw new InvalidOperationException();
        while (true)
        {
            using var socket = await _listener.AcceptTcpClientAsync(cancellation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var stream = socket.GetStream();
            string line;
            try { line = await ReadRequestLineAsync(stream, timeout.Token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { cancellation.ThrowIfCancellationRequested(); continue; }
            var parts = line.Split(' ');
            if (parts.Length != 3 || parts[0] != "GET" || !parts[1].StartsWith("/callback?", StringComparison.Ordinal))
            { await ReplyAsync(stream, false, timeout.Token); continue; }
            var query = ParseQuery(parts[1][parts[1].IndexOf('?')..]);
            if (!query.TryGetValue("state", out var state) || !ValidState(state))
            { await ReplyAsync(stream, false, timeout.Token); continue; }
            if (query.ContainsKey("error"))
            { await ReplyAsync(stream, false, timeout.Token); throw new AuthenticationRequiredException(); }
            if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            { await ReplyAsync(stream, false, timeout.Token); continue; }
            try
            {
                await client.CompleteLoginAsync(code, _verifier, RedirectUri, _state, cancellation);
                await ReplyAsync(stream, true, cancellation); return;
            }
            catch { try { await ReplyAsync(stream, false, cancellation); } catch (IOException) { } throw; }
        }
    }

    public Task CompleteCodeAsync(string text, OAuthUsageClient client, CancellationToken cancellation)
    {
        var parts = text.Trim().Split('#', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || !ValidState(parts[1]))
            throw new FormatException("이번 로그인에서 표시된 코드 전체(code#state)를 붙여 넣어 주세요.");
        return client.CompleteLoginAsync(parts[0], _verifier, RedirectUri, _state, cancellation);
    }

    private bool ValidState(string state) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(_state));
    public static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = field.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0]);
            if (!result.TryAdd(key, pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "")) return [];
        }
        return result;
    }
    private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellation)
    {
        var bytes = new List<byte>(); var buffer = new byte[1];
        // Drain bounded headers before closing: unread bytes cause a TCP reset on Windows.
        while (bytes.Count < 16384 && await stream.ReadAsync(buffer, cancellation) > 0)
        {
            bytes.Add(buffer[0]);
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n')
                return Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n", 2)[0];
        }
        return "";
    }
    private static async Task ReplyAsync(NetworkStream stream, bool success, CancellationToken cancellation)
    {
        var html = success ? "<h2>Claude widget connected</h2><p>You can close this tab.</p>" : "<h2>Sign-in not completed</h2><p>Return to the widget.</p>";
        var body = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8>" + html);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(success ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header, cancellation); await stream.WriteAsync(body, cancellation);
    }
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public void Dispose() => _listener?.Stop();
}
