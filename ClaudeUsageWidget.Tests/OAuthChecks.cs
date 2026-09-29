using System.Net;
using System.Text;
using ClaudeUsageWidget.Core;

internal static class OAuthChecks
{
    private const string Usage = """{"limits":[{"kind":"session","percent":4,"resets_at":"2026-09-28T10:00:00Z"},{"kind":"weekly_scoped","percent":12,"scope":{"model":{"display_name":"Fable"}}}]}""";
    private static int _passed;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); _passed++; Console.WriteLine("PASS OAuth: " + name); }
    private static OAuthCredentials Token(string name, bool expired = false, string? refresh = null) => new()
        { AccessToken = name, RefreshToken = refresh, ExpiresAt = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1) };
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Store : IOAuthTokenStore
    {
        public OAuthCredentials? Value;
        public int Saves;
        public OAuthCredentials? Load() => Value;
        public void Save(OAuthCredentials value) { Value = value; Saves++; }
        public void Delete() { Value = null; }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        { Calls++; return Task.FromResult(respond(request)); }
    }
    private sealed class PendingHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellation);
            throw new Exception("Unreachable");
        }
    }

    public static async Task RunAsync()
    {
        var snapshot = UsageParser.Parse(Usage, DateTimeOffset.UtcNow);
        Check(snapshot.FiveHour?.Percent == 4 && snapshot.Fable?.Percent == 12, "session and nested Fable percent schema");
        var inactive = UsageParser.Parse("""{"limits":[{"kind":"session","percent":0},{"kind":"weekly_scoped","is_active":false,"percent":80,"scope":{"model":{"display_name":"Fable"}}}]}""", DateTimeOffset.UtcNow);
        Check(inactive.Fable is null && inactive.FiveHour?.Percent == 0, "inactive Fable not displayed; zero is valid");
        Check(ClaudeCodeCredentials.Parse("""{"claudeAiOauth":{"accessToken":"fake","refreshToken":"do-not-copy","scopes":["user:profile"]}}""")?.RefreshToken is null, "shared refresh token never copied");
        Check(ClaudeCodeCredentials.Parse("""{"claudeAiOauth":{"accessToken":"fake","scopes":["user:inference"]}}""") is null, "inference-only credentials skipped");
        Check(ClaudeCodeCredentials.Read(Path.Combine(Environment.CurrentDirectory, "nonexistent-credentials-test.json")) is null, "missing CLI credentials safe");
        Check(ClaudeCodeCredentials.Parse("""{"claudeAiOauth":{"accessToken":"fake","expiresAt":null}}""") is not null, "null expiry safe");

        var store = new Store { Value = Token("own") };
        var handler = new Handler(r => { Check(r.Headers.Authorization?.Parameter == "cli", "CLI selected before widget credentials"); return Json(Usage); });
        using (var client = new OAuthUsageClient(store, () => Token("cli"), handler))
        { await client.FetchAsync(); Check(client.Source == "Claude Code" && store.Saves == 0, "CLI source and no credential writes"); }

        handler = new Handler(r => r.Headers.Authorization?.Parameter == "cli" ? Json("{}", HttpStatusCode.Unauthorized) : Json(Usage));
        using (var client = new OAuthUsageClient(store, () => Token("cli"), handler))
        { await client.FetchAsync(); await client.FetchAsync(); Check(client.Source == "브라우저" && handler.Calls == 3, "401 falls back and rejected CLI not retried until changed"); }

        handler = new Handler(r => { Check(r.Headers.Authorization?.Parameter == "own", "expired CLI skipped"); return Json(Usage); });
        using (var client = new OAuthUsageClient(store, () => Token("expired", true), handler)) await client.FetchAsync();
        handler = new Handler(_ => Json(Usage));
        using (var client = new OAuthUsageClient(new Store(), () => null, handler))
        {
            var required = false; try { await client.FetchAsync(); } catch (AuthenticationRequiredException) { required = true; }
            Check(required && handler.Calls == 0, "missing credentials requests browser without network");
        }

        handler = new Handler(_ => { var r = Json("{}", HttpStatusCode.TooManyRequests); r.Headers.TryAddWithoutValidation("Retry-After", "123"); return r; });
        using (var client = new OAuthUsageClient(store, () => Token("cli"), handler))
        {
            var limited = false; try { await client.FetchAsync(); } catch (UsageConnectionException ex) { limited = ex.StatusCode == 429 && ex.RetryAfter == 123; }
            Check(limited && handler.Calls == 1, "429 preserved without switching credentials");
            try { await client.FetchAsync(); } catch (UsageConnectionException) { }
            Check(handler.Calls == 1, "429 cooldown also blocks reconnect attempts");
        }

        store = new Store { Value = Token("old-own", true, "own-refresh") };
        handler = new Handler(r => r.Method == HttpMethod.Post ? Json("""{"access_token":"new-own","refresh_token":"rotated","expires_in":3600}""") : Json(Usage));
        using (var client = new OAuthUsageClient(store, () => null, handler))
        {
            await Task.WhenAll(client.FetchAsync(), client.FetchAsync());
            Check(store.Saves == 1 && store.Value?.RefreshToken == "rotated" && handler.Calls == 3, "concurrent refresh serialized and rotated token saved");
        }

        store = new Store { Value = Token("own-disconnect") };
        var codeReads = 0;
        handler = new Handler(_ => Json(Usage));
        using (var client = new OAuthUsageClient(store, () => { codeReads++; return Token("cli-preserved"); }, handler))
        {
            await client.FetchAsync();
            await client.DisconnectAsync();
            var blocked = false; try { await client.FetchAsync(); } catch (AuthenticationRequiredException) { blocked = true; }
            Check(blocked && store.Value is null && client.Source == "" && codeReads == 1 && handler.Calls == 1,
                "disconnect deletes own token and blocks CLI reads and HTTP until explicit reconnect");
            client.EnableConnection(); await client.FetchAsync();
            Check(client.Source == "Claude Code" && codeReads == 2, "explicit reconnect reuses unchanged CLI credential");
        }
        store = new Store { Value = Token("expired-disconnect", true, "refresh-disconnect") };
        var pending = new PendingHandler();
        using (var client = new OAuthUsageClient(store, () => null, pending))
        {
            var fetch = client.FetchAsync(); await pending.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
            try { await fetch; } catch (OperationCanceledException) { }
            Check(store.Value is null && store.Saves == 0, "disconnect during refresh cancels request and cannot recreate deleted token");
        }
        handler = new Handler(_ => Json(Usage));
        using (var client = new OAuthUsageClient(new Store(), () => throw new Exception("CLI must not be read"), handler, disconnected: true))
        {
            var blocked = false; try { await client.FetchAsync(); } catch (AuthenticationRequiredException) { blocked = true; }
            Check(blocked && handler.Calls == 0, "restored disconnected state blocks existing CLI credentials");
        }

        using (var login = new BrowserOAuthLogin())
        {
            var fields = BrowserOAuthLogin.ParseQuery(login.AuthorizeUri.Query);
            Check(fields["code_challenge_method"] == "S256" && fields["scope"] == "user:profile", "PKCE and read-only profile scope");
            store = new Store(); handler = new Handler(_ => Json("""{"access_token":"test-only","expires_in":3600}"""));
            using var client = new OAuthUsageClient(store, () => null, handler);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var waiting = login.WaitAndCompleteAsync(client, timeout.Token);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            using var invalid = await http.GetAsync(login.RedirectUri + "?code=fake&state=wrong", timeout.Token);
            Check(invalid.StatusCode == HttpStatusCode.BadRequest && handler.Calls == 0, "invalid state cannot exchange credentials");
            using var good = await http.GetAsync(login.RedirectUri + "?code=fake&state=" + fields["state"], timeout.Token);
            await waiting;
            Check(good.IsSuccessStatusCode && store.Saves == 1, "real loopback callback exchanges and saves");
        }
        using (var manual = new BrowserOAuthLogin(true))
        {
            var fields = BrowserOAuthLogin.ParseQuery(manual.AuthorizeUri.Query);
            store = new Store(); handler = new Handler(_ => Json("""{"access_token":"manual-only","expires_in":3600}"""));
            using var client = new OAuthUsageClient(store, () => null, handler);
            var rejected = false; try { await manual.CompleteCodeAsync("code#wrong", client, default); } catch (FormatException) { rejected = true; }
            Check(rejected && handler.Calls == 0, "pasted code requires matching state");
            await manual.CompleteCodeAsync("code#" + fields["state"], client, default);
            Check(store.Saves == 1 && manual.RedirectUri.StartsWith("https://platform.claude.com/"), "manual callback fallback saves own credentials");
        }
        using (var cancelled = new BrowserOAuthLogin())
        {
            handler = new Handler(_ => Json("{}"));
            using var client = new OAuthUsageClient(new Store(), () => null, handler);
            using var timeout = new CancellationTokenSource();
            var waiting = cancelled.WaitAndCompleteAsync(client, timeout.Token); timeout.Cancel();
            var stopped = false; try { await waiting; } catch (OperationCanceledException) { stopped = true; }
            Check(stopped && handler.Calls == 0, "cancelling browser login stops listener wait without network");
        }
        var folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "oauth-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "oauth.dat");
        try
        {
            var protectedStore = new WindowsOAuthTokenStore(path);
            protectedStore.Delete();
            Check(!Directory.Exists(folder), "disconnect on fresh installation succeeds without creating credential directory");
            protectedStore.Save(Token("test-secret", refresh: "test-refresh"));
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("test-secret") && protectedStore.Load()?.RefreshToken == "test-refresh", "Windows DPAPI round trip without plaintext on disk");
            File.WriteAllText(path, "invalid"); Check(protectedStore.Load() is null, "corrupt encrypted store falls back safely");
            protectedStore.Delete(); protectedStore.Delete();
            Check(!File.Exists(path), "credential deletion is idempotent");
            var settingsPath = Path.Combine(folder, "settings.json");
            SettingsStore.Save(settingsPath, new WidgetSettings { AutoConnect = false, ConnectionDisabled = true });
            var disconnected = SettingsStore.Load(settingsPath);
            Check(disconnected.ConnectionDisabled && !disconnected.AutoConnect, "disconnect state persists across restart");
            File.Delete(settingsPath);
        }
        finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(folder)) Directory.Delete(folder); }
        Console.WriteLine($"OAuth: {_passed} checks passed.");
    }

    public static async Task ProbeAsync()
    {
        var credentials = ClaudeCodeCredentials.Read();
        Console.WriteLine("Claude Code usable credential present: " + (credentials is { IsExpired: false }));
        if (credentials is not { IsExpired: false }) return;
        using var client = new OAuthUsageClient(new Store(), () => credentials);
        try
        {
            var result = UsageParser.Parse(await client.FetchAsync(), DateTimeOffset.UtcNow);
            Console.WriteLine($"Live OAuth usage: SUCCESS; five-hour={result.FiveHour?.Percent}; Fable={result.Fable?.Percent}; reset-present={result.FiveHour?.ResetsAt is not null}");
        }
        catch (AuthenticationRequiredException) { Console.WriteLine("Live OAuth usage: browser login required."); }
        catch (UsageConnectionException ex) { Console.WriteLine($"Live OAuth usage: HTTP {ex.StatusCode}; retry-after={ex.RetryAfter}"); }
        catch (Exception ex) { Console.WriteLine("Live OAuth usage: " + ex.GetType().Name); }
    }
}
