using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeUsageWidget.Core;

namespace ClaudeUsageWidget;

public sealed class ConnectionWindow : Window
{
    private readonly OAuthUsageClient? _client;
    private readonly TextBlock _status;
    private readonly Button _manual;
    private readonly TextBox _code;
    private readonly Button _submit;
    private BrowserOAuthLogin? _login;
    private CancellationTokenSource? _attempt;
    private bool _closed;
    public string? UsageJson { get; private set; }

    public ConnectionWindow(OAuthUsageClient? client = null)
    {
        _client = client;
        Title = "Claude 연결";
        Width = 510; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(33, 35, 32)); Foreground = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Claude Code 우선 · 브라우저 로그인", FontSize = 20, Margin = new Thickness(0, 0, 0, 16) });
        _status = new TextBlock { Text = "Claude Code 인증을 먼저 확인합니다. 사용할 수 없으면 평소 브라우저에서 로그인합니다.", TextWrapping = TextWrapping.Wrap, LineHeight = 24, Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(_status);
        _manual = new Button { Content = "브라우저에서 돌아오지 않으면: 코드로 연결", Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed };
        _manual.Click += async (_, _) => await StartBrowserAsync(true); panel.Children.Add(_manual);
        _code = new TextBox { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12) }; panel.Children.Add(_code);
        _submit = new Button { Content = "코드 확인", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12) };
        _submit.Click += async (_, _) => await SubmitCodeAsync(); panel.Children.Add(_submit);
        var close = new Button { Content = "닫기" }; close.Click += (_, _) => Close(); panel.Children.Add(close);
        Closed += (_, _) => { _closed = true; _attempt?.Cancel(); _login?.Dispose(); };
        Loaded += async (_, _) =>
        {
            if (_client is null) return;
            _attempt = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try { await FinishAsync(_attempt.Token); }
            catch (AuthenticationRequiredException) { if (!_closed) await StartBrowserAsync(false); }
            catch (Exception ex) { ShowError(ex); }
        };
    }

    private async Task StartBrowserAsync(bool manual)
    {
        _attempt?.Cancel(); _login?.Dispose();
        var attempt = new CancellationTokenSource(TimeSpan.FromMinutes(5)); _attempt = attempt;
        try
        {
            _login = new BrowserOAuthLogin(manual);
            _manual.Visibility = Visibility.Visible;
            _code.Visibility = _submit.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
            _code.Clear();
            _status.Text = manual ? "브라우저에서 승인을 마친 뒤 표시되는 코드 전체를 아래에 붙여 넣어 주세요."
                : "평소 브라우저에서 Claude 로그인을 완료해 주세요. 인증이 끝나면 이 창이 자동으로 닫힙니다.";
            Process.Start(new ProcessStartInfo(_login.AuthorizeUri.AbsoluteUri) { UseShellExecute = true });
            if (manual) { _code.Focus(); return; }
            await _login.WaitAndCompleteAsync(_client!, attempt.Token);
            await FinishAsync(attempt.Token);
        }
        catch (Exception ex) { if (_attempt == attempt) ShowError(ex); }
    }
    private async Task SubmitCodeAsync()
    {
        if (_client is null || _login is null || _attempt is null) return;
        _submit.IsEnabled = _manual.IsEnabled = false;
        try
        {
            await _login.CompleteCodeAsync(_code.Text, _client, _attempt.Token); _code.Clear();
            await FinishAsync(_attempt.Token);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _submit.IsEnabled = _manual.IsEnabled = true; }
    }
    private async Task FinishAsync(CancellationToken cancellation)
    {
        var json = await _client!.FetchAsync(cancellation);
        UsageParser.Parse(json, DateTimeOffset.UtcNow);
        if (_closed) return;
        UsageJson = json; DialogResult = true;
    }
    private void ShowError(Exception error)
    {
        if (_closed) return;
        _status.Text = error is OperationCanceledException ? "로그인 대기 시간이 지났습니다. 코드로 연결하거나 창을 닫고 다시 연결해 주세요."
            : error is AuthenticationRequiredException ? "인증이 완료되지 않았습니다. 코드로 연결을 다시 시도해 주세요."
            : error is UsageConnectionException or FormatException ? error.Message
            : "연결하지 못했습니다. 네트워크를 확인한 뒤 다시 연결해 주세요.";
        _manual.Visibility = error is UsageConnectionException { StatusCode: 429 } ? Visibility.Collapsed : Visibility.Visible;
    }
}
