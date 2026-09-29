using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClaudeUsageWidget.Core;

namespace ClaudeUsageWidget;

public partial class MainWindow : Window
{
    private static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageWidget");
    private static readonly string SettingsPath = Path.Combine(DataFolder, "settings.json");
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly bool _smokeTest;
    private WidgetSettings _settings;
    private UsageSnapshot? _snapshot;
    private OAuthUsageClient? _oauth;
    private DateTimeOffset _nextFetch = DateTimeOffset.UtcNow;
    private DateTimeOffset _rateLimitUntil = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastExpiredReset;
    private bool _busy, _closing, _autoEnabled, _signingOut;
    private bool _openingLogin;
    private bool _shutdownComplete;
    private int _failures, _generation;

    public MainWindow(bool smokeTest = false)
    {
        _smokeTest = smokeTest;
        _settings = smokeTest ? new() : SettingsStore.Load(SettingsPath);
        _autoEnabled = !smokeTest && _settings.AutoConnect && !_settings.ConnectionDisabled;
        InitializeComponent();
        ApplySettings();
        Loaded += OnLoaded;
        _clock.Tick += async (_, _) =>
        {
            RenderClock();
            if (!_settings.ManualMode && _snapshot?.FiveHour?.ResetsAt is { } reset &&
                reset <= DateTimeOffset.UtcNow && reset != _lastExpiredReset)
            { _lastExpiredReset = reset; _nextFetch = DateTimeOffset.UtcNow; }
            if (_autoEnabled && DateTimeOffset.UtcNow >= _nextFetch) await RefreshAsync();
        };
        Closing += async (_, args) =>
        {
            if (_shutdownComplete) return;
            args.Cancel = true;
            if (_closing) return;
            _closing = true; _generation++; _clock.Stop();
            _settings.Left = Left; _settings.Top = Top;
            SaveSettings();
            try { _oauth?.Dispose(); }
            finally
            {
                _shutdownComplete = true;
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Restore only onto the primary work area; a removed monitor must not strand the widget.
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(_settings.Left ?? area.Right - ActualWidth - 24, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
        Top = Math.Clamp(_settings.Top ?? area.Top + 40, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
        if (_smokeTest) { await RunSmokeTestAsync(); return; }
        _oauth = new OAuthUsageClient(new WindowsOAuthTokenStore(Path.Combine(DataFolder, "oauth.dat")), disconnected: _settings.ConnectionDisabled);
        _clock.Start();
        if (_autoEnabled) await RefreshAsync();
    }

    private void ApplySettings()
    {
        Opacity = _settings.Opacity;
        Topmost = _settings.AlwaysOnTop;
        PinButton.Content = Topmost ? "고정 ●" : "고정";
        PinButton.Foreground = Topmost ? (Brush)FindResource("Accent") : (Brush)FindResource("Ink");
        if (_settings.ManualMode)
        {
            _snapshot = new(new(_settings.ManualFiveHour, _settings.ManualReset), new(_settings.ManualFable, null), DateTimeOffset.UtcNow);
            Status.Text = "직접 입력 모드 · 자동 조회가 중지되었습니다.";
        }
        else if (_settings.ConnectionDisabled)
            Status.Text = "연결이 해제되어 있습니다. ‘연결’을 눌러 다시 연결해 주세요.";
        RenderUsage();
    }

    private async Task RefreshAsync()
    {
        if (_busy || _closing || _signingOut || _openingLogin || _settings.ManualMode || _settings.ConnectionDisabled) return;
        if (DateTimeOffset.UtcNow < _rateLimitUntil)
        { Status.Text = "요청 제한으로 대기 중입니다. 마지막 확인값을 표시합니다."; return; }
        _busy = true;
        var generation = _generation;
        RefreshButton.IsEnabled = false;
        Status.Text = "사용량을 조회하고 있습니다…";
        try
        {
            var json = await _oauth!.FetchAsync();
            if (_closing || generation != _generation) return;
            _snapshot = UsageParser.Parse(json, DateTimeOffset.UtcNow);
            _autoEnabled = true;
            if (!_settings.AutoConnect) { _settings.AutoConnect = true; SaveSettings(); }
            _failures = 0;
            _nextFetch = DateTimeOffset.UtcNow.AddSeconds(_settings.RefreshSeconds);
            Status.Text = $"연결됨 · {_oauth.Source} · 자동 조회 중";
            RenderUsage();
        }
        catch (Exception error)
        {
            if (_closing || generation != _generation) return;
            _failures++;
            var connection = error as UsageConnectionException;
            _nextFetch = DateTimeOffset.UtcNow + UsageClock.RetryDelay(_settings.RefreshSeconds, _failures, connection?.RetryAfter);
            if (error is AuthenticationRequiredException) _autoEnabled = false;
            if (connection?.StatusCode == 429) _rateLimitUntil = _nextFetch;
            Status.Text = error is UsageConnectionException or AuthenticationRequiredException or FormatException ? error.Message :
                error is System.Text.Json.JsonException ? "사용량 응답을 읽지 못했습니다. 잠시 뒤 다시 조회합니다." :
                "사용량 서버에 연결하지 못했습니다. 네트워크를 확인해 주세요.";
            RenderClock();
        }
        finally
        {
            _busy = false;
            if (!_closing) RefreshButton.IsEnabled = !_settings.ManualMode && !_settings.ConnectionDisabled;
        }
    }

    private void RenderUsage()
    {
        FivePercent.Text = PercentText(_snapshot?.FiveHour);
        FiveProgress.Value = _snapshot?.FiveHour?.Percent ?? 0;
        FablePercent.Text = PercentText(_snapshot?.Fable);
        FableProgress.Value = _snapshot?.Fable?.Percent ?? 0;
        FiveProgress.Foreground = _snapshot?.FiveHour?.Percent >= 90 ? Brushes.Salmon : (Brush)FindResource("Accent");
        FableInfo.Text = _settings.ManualMode ? "사용자가 입력한 값입니다." :
            _snapshot is null ? "계정에서 제공하는 한도를 표시합니다." :
            _snapshot.Fable is null ? "응답에 Fable 항목이 없습니다. 설정에서 직접 입력할 수 있습니다." :
            _snapshot.Fable.ResetsAt is { } fableReset ? $"초기화 {fableReset.ToLocalTime():M월 d일 HH:mm}" : "Fable 전용 사용 한도";
        RefreshButton.IsEnabled = !_busy && !_settings.ManualMode && !_settings.ConnectionDisabled;
        RenderClock();
    }

    private static string PercentText(UsageBucket? bucket) => bucket is null ? "—" : bucket.Percent.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private void RenderClock()
    {
        var now = DateTimeOffset.UtcNow;
        Countdown.Text = UsageClock.Remaining(_snapshot?.FiveHour?.ResetsAt, now);
        ResetTime.Text = _snapshot?.FiveHour?.ResetsAt is { } reset ? $"{reset.ToLocalTime():M월 d일 HH:mm:ss} 초기화 예정" :
            _settings.ManualMode ? "설정에서 초기화 시각을 입력해 주세요." : "서버에서 초기화 시각을 받으면 표시합니다.";
        if (_settings.ManualMode) { Updated.Text = "수동 값 · 남은 시간만 초 단위로 갱신합니다."; return; }
        var next = Math.Max(0, (int)Math.Ceiling((Max(_nextFetch, _rateLimitUntil) - now).TotalSeconds));
        if (_snapshot is null) { Updated.Text = _autoEnabled ? $"아직 조회하지 않았습니다. 다음 시도 {next}초 후" : "연결 버튼을 누르면 인증을 확인합니다."; return; }
        var age = now - _snapshot.RetrievedAt;
        var stale = _failures > 0 || age.TotalSeconds > Math.Max(60, _settings.RefreshSeconds * 2);
        Updated.Text = $"{(stale ? "마지막 확인값 · " : "")}조회 {_snapshot.RetrievedAt.ToLocalTime():HH:mm:ss} · 다음 {next}초 후";
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private void OpenLogin(object sender, RoutedEventArgs e)
    {
        if (_openingLogin || _signingOut) return;
        _openingLogin = true;
        _generation++;
        try
        {
            if (DateTimeOffset.UtcNow < _rateLimitUntil) { Status.Text = "서버 요청 제한이 끝난 뒤 다시 연결해 주세요."; return; }
            _oauth?.EnableConnection();
            var connection = new ConnectionWindow(_oauth) { Owner = this, Topmost = Topmost };
            if (connection.ShowDialog() != true || _closing || connection.UsageJson is null) return;
            _snapshot = UsageParser.Parse(connection.UsageJson, DateTimeOffset.UtcNow);
            _autoEnabled = true;
            _settings.AutoConnect = true;
            _settings.ConnectionDisabled = false;
            _failures = 0;
            _nextFetch = DateTimeOffset.UtcNow.AddSeconds(_settings.RefreshSeconds);
            Status.Text = _settings.ManualMode ? "자동 조회하려면 설정에서 직접 입력 모드를 꺼 주세요."
                : $"연결됨 · {_oauth!.Source} · 자동 조회 중";
            if (_settings.ManualMode) ApplySettings(); else RenderUsage();
            SaveSettings();
        }
        catch (Exception)
        {
            if (_closing) return;
            Status.Text = "연결을 완료하지 못했습니다. 다시 연결해 주세요.";
        }
        finally
        {
            _openingLogin = false;
            if (_settings.ConnectionDisabled) _oauth?.SuspendConnection();
            if (_oauth is not null) _rateLimitUntil = Max(_rateLimitUntil, _oauth.RetryUntil);
        }
    }

    private async void RefreshNow(object sender, RoutedEventArgs e)
    {
        if (_settings.ConnectionDisabled || _signingOut) return;
        _autoEnabled = true;
        await RefreshAsync();
    }

    private async void OpenSettings(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(_settings, value => Opacity = value, SignOutAsync) { Owner = this, Topmost = Topmost };
        if (settings.ShowDialog() == true)
        {
            _generation++;
            var modeChanged = _settings.ManualMode != settings.Result.ManualMode;
            settings.Result.AutoConnect = _settings.AutoConnect;
            settings.Result.ConnectionDisabled = _settings.ConnectionDisabled;
            _settings = settings.Result;
            if (modeChanged)
            {
                _snapshot = null;
                _failures = 0;
                _lastExpiredReset = null;
            }
            _nextFetch = DateTimeOffset.UtcNow;
            ApplySettings();
            SaveSettings();
            if (!_settings.ManualMode)
            {
                Status.Text = _autoEnabled ? "자동 조회를 준비합니다." : "연결이 해제되어 있습니다. ‘연결’을 눌러 다시 연결해 주세요.";
                if (_autoEnabled) await RefreshAsync();
            }
        }
        else Opacity = _settings.Opacity;
    }

    private async Task SignOutAsync()
    {
        _generation++;
        _signingOut = true;
        try
        {
            _autoEnabled = false;
            _settings.AutoConnect = false;
            _settings.ConnectionDisabled = true;
            if (!_settings.ManualMode) _snapshot = null;
            _failures = 0;
            _lastExpiredReset = null;
            Status.Text = "연결을 해제하고 있습니다…";
            RenderUsage();
            try
            {
                if (_oauth is not null) await _oauth.DisconnectAsync();
            }
            finally
            {
                // Persist the block even if deleting credentials failed.
                if (!_smokeTest) SettingsStore.Save(SettingsPath, _settings);
            }
            Status.Text = "연결이 해제되었습니다. ‘연결’을 눌러 다시 연결할 수 있습니다.";
        }
        catch { Status.Text = "조회는 중지됐지만 연결 해제를 완료하지 못했습니다. 설정에서 다시 시도해 주세요."; throw; }
        finally { _signingOut = false; }
    }

    private void TogglePin(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        ApplySettings();
        SaveSettings();
    }

    private void SaveSettings()
    {
        if (_smokeTest) return;
        try { SettingsStore.Save(SettingsPath, _settings); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { if (!_closing) Status.Text = "설정을 저장하지 못했습니다. 저장 폴더 권한을 확인해 주세요."; }
    }

    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
    private void CloseWidget(object sender, RoutedEventArgs e) => Close();

    private async Task RunSmokeTestAsync()
    {
        try
        {
            var folder = Path.Combine(Environment.CurrentDirectory, "artifacts");
            Directory.CreateDirectory(folder);
            _snapshot = new(new(42, DateTimeOffset.UtcNow.AddHours(2).AddMinutes(18).AddSeconds(39)), new(17, DateTimeOffset.UtcNow.AddDays(3)), DateTimeOffset.UtcNow);
            Status.Text = "연결됨 · 자동 조회 중";
            _nextFetch = DateTimeOffset.UtcNow.AddSeconds(30);
            RenderUsage();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Capture(this, Path.Combine(folder, "widget-preview.png"));
            var settings = new SettingsWindow(_settings, _ => { }, () => Task.CompletedTask) { Owner = this };
            settings.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Capture(settings, Path.Combine(folder, "settings-preview.png"));
            settings.Close();
            var connection = new ConnectionWindow { Owner = this };
            connection.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Capture(connection, Path.Combine(folder, "connection-preview.png"));
            connection.Close();
            _autoEnabled = true; _settings.AutoConnect = true;
            await SignOutAsync();
            if (_autoEnabled || _settings.AutoConnect || !_settings.ConnectionDisabled || _snapshot is not null || RefreshButton.IsEnabled)
                throw new Exception("Disconnected UI state failed");
            await RefreshAsync();
            if (_snapshot is not null) throw new Exception("Disconnected refresh restored usage");
            Capture(this, Path.Combine(folder, "disconnected-preview.png"));
            _settings.ManualMode = true;
            _settings.ManualFiveHour = 99.5;
            _settings.ManualFable = 100;
            _settings.ManualReset = DateTimeOffset.UtcNow.AddMinutes(-1);
            ApplySettings();
            if (Countdown.Text != "초기화 확인 대기" || RefreshButton.IsEnabled) throw new Exception("Manual UI state failed");
            Capture(this, Path.Combine(folder, "manual-preview.png"));
            File.WriteAllText(Path.Combine(folder, "smoke-test.txt"), "PASS: widget and settings rendered; disconnect clears usage and blocks refresh; manual mode and expired reset verified.");
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "smoke-error.txt"), error.ToString());
            Environment.ExitCode = 1;
        }
        finally { Close(); }
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
