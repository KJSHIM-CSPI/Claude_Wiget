using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;

namespace ClaudeUsageWidget.Core;

public sealed class UsageConnectionException(string message, int status = 0, int? retryAfter = null) : Exception(message)
{
    public int StatusCode { get; } = status;
    public int? RetryAfter { get; } = retryAfter;
}

public sealed class BrowserUnavailableException(string message) : Exception(message);

public sealed record BrowserInstallation(string Name, string Executable)
{
    public static BrowserInstallation Find(string? preferred = null)
    {
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
        foreach (var browser in new[] { ("Chrome", @"Google\Chrome\Application\chrome.exe"), ("Edge", @"Microsoft\Edge\Application\msedge.exe") })
        {
            if (preferred is not null && browser.Item1 != preferred) continue;
            foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
            {
                var path = Path.Combine(root, browser.Item2);
                if (File.Exists(path)) return new(browser.Item1, path);
            }
        }
        throw new BrowserUnavailableException(preferred is null ? "Chrome 또는 Microsoft Edge를 설치해 주세요." : $"이전 로그인에 사용한 {preferred} 실행 파일을 찾지 못했습니다.");
    }
}

public sealed class BrowserSession : IDisposable
{
    private const string UsageUrl = "https://claude.ai/settings/usage";
    private readonly string _profilePath;
    private readonly BrowserInstallation _installation;
    private readonly bool _offlineTest;
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(3) };
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private BrowserEndpoint? _lastEndpoint;
    public string BrowserName => _installation.Name;
    private sealed record Tab(string Id, string Url, Uri Socket);

    public BrowserSession(string profileRoot, BrowserInstallation? installation = null, bool offlineTest = false)
    {
        _installation = installation ?? BrowserInstallation.Find();
        _profilePath = Path.Combine(Path.GetFullPath(profileRoot), _installation.Name);
        _offlineTest = offlineTest;
    }

    public async Task ShowLoginAsync()
    {
        await _gate.WaitAsync(_lifetime.Token);
        try
        {
            var endpoint = await EnsureBrowserAsync(launch: true);
            var tabs = await TabsAsync(endpoint);
            var tab = ChooseClaudeTab(tabs) ?? tabs.FirstOrDefault(t => IsGoogleLogin(t.Url));
            if (tab is null)
            {
                var created = await Command(endpoint.BrowserSocket, "Target.createTarget", new { url = _offlineTest ? "about:blank" : UsageUrl });
                var targetId = created.GetProperty("targetId").GetString();
                tab = (await TabsAsync(endpoint)).FirstOrDefault(t => t.Id == targetId);
            }
            if (tab is not null && !_offlineTest)
            {
                await SetWindowStateAsync(endpoint, tab, "normal");
                await Command(tab.Socket, "Page.bringToFront");
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<string> FetchAsync()
    {
        await _gate.WaitAsync(_lifetime.Token);
        try
        {
            var endpoint = await EnsureBrowserAsync(launch: Directory.Exists(_profilePath), minimized: true);
            var tabs = await TabsAsync(endpoint);
            var tab = ChooseClaudeTab(tabs);
            if (tab is null && !tabs.Any(t => IsGoogleLogin(t.Url)))
            {
                await Command(endpoint.BrowserSocket, "Target.createTarget", new { url = UsageUrl, background = true });
                throw new UsageConnectionException("브라우저에서 사용량 페이지를 여는 중입니다. 잠시 후 연결합니다.");
            }
            if (tab is null) throw new UsageConnectionException("브라우저에서 Claude 로그인을 완료해 주세요. 연결할 Claude 탭을 기다리고 있습니다.", 401);
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeUsageWidget.Core.Browser.usage.js")!;
            using var reader = new StreamReader(stream);
            var script = await reader.ReadToEndAsync(_lifetime.Token);
            JsonElement evaluated;
            try { evaluated = await Command(tab.Socket, "Runtime.evaluate", new { expression = script, awaitPromise = true, returnByValue = true }); }
            catch (CdpProtocolException) { throw new UsageConnectionException("로그인 페이지가 이동 중입니다. 잠시 후 자동으로 확인합니다."); }
            if (evaluated.TryGetProperty("exceptionDetails", out _) ||
                !evaluated.TryGetProperty("result", out var result) || !result.TryGetProperty("value", out var message))
                throw new UsageConnectionException("브라우저 페이지가 이동 중이거나 응답하지 않습니다. 잠시 후 다시 확인합니다.");
            if (message.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True && message.TryGetProperty("usage", out var usage))
                return usage.GetRawText();
            var status = ReadInt(message, "status") ?? 0;
            var kind = ReadString(message, "kind");
            var stage = ReadString(message, "stage");
            throw new UsageConnectionException(ConnectionFailure.Describe(kind, stage, status), status, ReadInt(message, "retryAfter"));
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        { throw new UsageConnectionException("브라우저 응답 시간이 초과되었습니다. 잠시 후 다시 확인합니다."); }
        finally { _gate.Release(); }
    }

    public async Task MinimizeAsync()
    {
        await _gate.WaitAsync(_lifetime.Token);
        try
        {
            var endpoint = await EnsureBrowserAsync(launch: false);
            var tab = ChooseClaudeTab(await TabsAsync(endpoint));
            if (tab is not null) await SetWindowStateAsync(endpoint, tab, "minimized");
        }
        finally { _gate.Release(); }
    }

    public async Task SignOutAsync()
    {
        await _gate.WaitAsync(_lifetime.Token);
        try
        {
            if (!Directory.Exists(_profilePath)) return;
            var existing = await TryEndpointAsync();
            var endpoint = existing ?? await EnsureBrowserAsync(launch: true, blank: true, minimized: true);
            try { await ClearSessionAsync(endpoint); }
            finally
            {
                if (existing is null)
                {
                    try { await Command(endpoint.BrowserSocket, "Browser.close"); }
                    catch (IOException) { }
                    catch (System.Net.WebSockets.WebSocketException) { }
                }
            }
        }
        finally { _gate.Release(); }
    }

    private async Task ClearSessionAsync(BrowserEndpoint endpoint)
    {
        // Stop authenticated pages first so in-flight navigation cannot immediately recreate the session.
        foreach (var tab in (await TabsAsync(endpoint)).Where(t => IsClaudeOrigin(t.Url) || IsGoogleLogin(t.Url)))
        {
            await Command(tab.Socket, "Page.stopLoading");
            await Command(tab.Socket, "Page.navigate", new { url = "about:blank" });
        }
        var created = await Command(endpoint.BrowserSocket, "Target.createTarget", new { url = "about:blank", background = true });
        var targetId = created.GetProperty("targetId").GetString();
        var cleanupTab = (await TabsAsync(endpoint)).First(t => t.Id == targetId);
        // clearDataForOrigin is a page-target command; clearCookies is supported on the browser endpoint.
        await Command(cleanupTab.Socket, "Storage.clearDataForOrigin", new { origin = "https://claude.ai", storageTypes = "all" });
        await Command(endpoint.BrowserSocket, "Storage.clearCookies");
    }

    private async Task SetWindowStateAsync(BrowserEndpoint endpoint, Tab tab, string state)
    {
        var window = await Command(endpoint.BrowserSocket, "Browser.getWindowForTarget", new { targetId = tab.Id });
        await Command(endpoint.BrowserSocket, "Browser.setWindowBounds", new { windowId = window.GetProperty("windowId").GetInt32(), bounds = new { windowState = state } });
    }

    private Task<JsonElement> Command(Uri address, string method, object? args = null) => CdpConnection.CommandAsync(address, method, args, _lifetime.Token);

    private async Task<BrowserEndpoint> EnsureBrowserAsync(bool launch, bool blank = false, bool minimized = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connected = await TryEndpointAsync();
        if (connected is not null) { _lastEndpoint = connected; return connected; }
        if (!launch) throw new UsageConnectionException("로그인 버튼으로 브라우저를 열어 주세요. 조회 중에는 브라우저를 닫지 말고 최소화해 주세요.");
        Directory.CreateDirectory(_profilePath);
        var start = new ProcessStartInfo(_installation.Executable) { UseShellExecute = false, WindowStyle = minimized ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal };
        foreach (var argument in new[] { $"--user-data-dir={_profilePath}", "--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1",
            "--no-first-run", "--no-default-browser-check", "--new-window" }) start.ArgumentList.Add(argument);
        if (minimized) start.ArgumentList.Add("--start-minimized");
        if (_offlineTest)
        {
            start.WindowStyle = ProcessWindowStyle.Hidden;
            foreach (var argument in new[] { "--headless=new", "--disable-background-networking", "--disable-component-update", "--disable-default-apps", "--disable-sync", "--no-proxy-server", "--host-resolver-rules=MAP * ~NOTFOUND" }) start.ArgumentList.Add(argument);
        }
        start.ArgumentList.Add(_offlineTest || blank ? "about:blank" : UsageUrl);
        using var process = Process.Start(start) ?? throw new BrowserUnavailableException("일반 브라우저를 실행하지 못했습니다.");
        var started = Stopwatch.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(250, _lifetime.Token);
            connected = await TryEndpointAsync();
            if (connected is not null) { _lastEndpoint = connected; return connected; }
        }
        throw new BrowserUnavailableException("브라우저 연결을 시작하지 못했습니다. Chrome 또는 Edge 실행 상태와 회사 브라우저 정책을 확인해 주세요.");
    }

    private async Task<BrowserEndpoint?> TryEndpointAsync()
    {
        try
        {
            var file = Path.Combine(_profilePath, "DevToolsActivePort");
            if (!File.Exists(file)) return null;
            var endpoint = BrowserEndpoint.Parse(await File.ReadAllTextAsync(file, _lifetime.Token));
            var version = await HttpJsonAsync(new Uri(endpoint.HttpBase, "json/version"));
            endpoint.ValidateSocket(version.GetProperty("webSocketDebuggerUrl").GetString()!, browser: true);
            _lastEndpoint = endpoint;
            return endpoint;
        }
        catch (Exception error) when (error is IOException or HttpRequestException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        { return null; }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested) { return null; }
    }

    private async Task<JsonElement> HttpJsonAsync(Uri address)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await content.ReadAsync(bytes, timeout.Token)) > 0)
        {
            if (buffer.Length + count > 2 * 1024 * 1024) throw new IOException("브라우저 연결 정보가 너무 큽니다.");
            buffer.Write(bytes, 0, count);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private async Task<List<Tab>> TabsAsync(BrowserEndpoint endpoint)
    {
        var result = await HttpJsonAsync(new Uri(endpoint.HttpBase, "json/list"));
        var tabs = new List<Tab>();
        foreach (var item in result.EnumerateArray())
        {
            if (ReadString(item, "type") != "page" || !item.TryGetProperty("webSocketDebuggerUrl", out var socket)) continue;
            tabs.Add(new(ReadString(item, "id"), ReadString(item, "url"), endpoint.ValidateSocket(socket.GetString()!)));
        }
        return tabs;
    }

    private static Tab? ChooseClaudeTab(IEnumerable<Tab> tabs) => tabs.Where(t => IsClaudeOrigin(t.Url))
        .OrderByDescending(t => new Uri(t.Url).AbsolutePath.StartsWith("/settings/usage", StringComparison.Ordinal)).FirstOrDefault();
    public static bool IsClaudeOrigin(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == "claude.ai" && uri.IsDefaultPort && uri.UserInfo.Length == 0;
    private static bool IsGoogleLogin(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "accounts.google.com";
    private static string ReadString(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static int? ReadInt(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    // Integration probe: the very same launch, endpoint verification and transport, on about:blank only.
    public async Task ProbeOfflineAsync()
    {
        if (!_offlineTest) throw new InvalidOperationException("Offline test profile required.");
        await _gate.WaitAsync(_lifetime.Token);
        BrowserEndpoint? endpoint = null;
        try
        {
            endpoint = await EnsureBrowserAsync(launch: true, blank: true);
            var tab = (await TabsAsync(endpoint)).First(t => t.Url == "about:blank");
            var result = await Command(tab.Socket, "Runtime.evaluate", new { expression = "Promise.resolve(6 * 7)", awaitPromise = true, returnByValue = true });
            if (result.GetProperty("result").GetProperty("value").GetInt32() != 42) throw new IOException("브라우저 실행 검증에 실패했습니다.");
            await SetWindowStateAsync(endpoint, tab, "minimized");
            await SetWindowStateAsync(endpoint, tab, "normal");
            await Command(tab.Socket, "Page.bringToFront");
            var reconnected = await EnsureBrowserAsync(launch: false);
            if (reconnected != endpoint) throw new IOException("전용 브라우저 재연결 검증에 실패했습니다.");
            await Command(tab.Socket, "Network.setCookie", new { name = "WidgetProbe", value = "synthetic-test-value", url = "https://claude.ai", secure = true });
            await ClearSessionAsync(endpoint);
            var cookies = await Command(tab.Socket, "Network.getCookies", new { urls = new[] { "https://claude.ai" } });
            if (cookies.GetProperty("cookies").GetArrayLength() != 0) throw new IOException("테스트 세션 삭제 검증에 실패했습니다.");
        }
        finally
        {
            if (endpoint is not null) await ShutdownAsync();
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _http.Dispose();
        // A connection reset can reuse the browser. App termination separately calls ShutdownAsync.
    }

    public async Task ShutdownAsync()
    {
        if (_disposed) return;
        _lifetime.Cancel();
        if (_lastEndpoint is { } endpoint)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await CdpConnection.CommandAsync(endpoint.BrowserSocket, "Browser.close", cancellationToken: timeout.Token); }
            catch (Exception error) when (error is IOException or System.Net.WebSockets.WebSocketException or OperationCanceledException) { }
        }
        Dispose();
    }
}
