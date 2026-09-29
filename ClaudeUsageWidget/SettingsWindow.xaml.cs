using System.Globalization;
using System.Windows;
using ClaudeUsageWidget.Core;

namespace ClaudeUsageWidget;

public partial class SettingsWindow : Window
{
    private readonly Action<double> _preview;
    private readonly Func<Task> _logout;
    private bool _ready;
    public WidgetSettings Result { get; private set; }

    public SettingsWindow(WidgetSettings settings, Action<double> preview, Func<Task> logout)
    {
        _preview = preview; _logout = logout; Result = settings.Copy();
        InitializeComponent();
        IntervalInput.Text = settings.RefreshSeconds.ToString(CultureInfo.InvariantCulture);
        TransparencyInput.Value = Math.Round((1 - settings.Opacity) * 100);
        OpacityLabel.Text = $"{TransparencyInput.Value:0}%";
        TopmostInput.IsChecked = settings.AlwaysOnTop;
        ManualInput.IsChecked = settings.ManualMode;
        FiveInput.Text = settings.ManualFiveHour.ToString(CultureInfo.InvariantCulture);
        FableInput.Text = settings.ManualFable.ToString(CultureInfo.InvariantCulture);
        ResetInput.Text = settings.ManualReset?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
        ManualFields.IsEnabled = settings.ManualMode;
        _ready = true;
    }

    private void PreviewTransparency(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        OpacityLabel.Text = $"{e.NewValue:0}%";
        _preview(1 - e.NewValue / 100);
    }

    private void ManualChanged(object sender, RoutedEventArgs e)
    {
        if (ManualFields is not null) ManualFields.IsEnabled = ManualInput.IsChecked == true;
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(IntervalInput.Text, out var interval) || interval < 10 || interval > 86400)
        { ErrorText.Text = "갱신 주기는 10~86,400 사이의 정수로 입력해 주세요."; return; }
        var five = Result.ManualFiveHour;
        var fable = Result.ManualFable;
        var reset = Result.ManualReset;
        if (ManualInput.IsChecked == true)
        {
            if (!Percent(FiveInput.Text, out five) || !Percent(FableInput.Text, out fable))
            { ErrorText.Text = "사용률은 0~100 사이의 숫자로 입력해 주세요."; return; }
            reset = null;
            if (!string.IsNullOrWhiteSpace(ResetInput.Text))
            {
                if (!DateTime.TryParseExact(ResetInput.Text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var local) || TimeZoneInfo.Local.IsInvalidTime(local))
                { ErrorText.Text = "초기화 시각을 yyyy-MM-dd HH:mm 형식으로 입력해 주세요."; return; }
                reset = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local));
            }
        }
        Result.RefreshSeconds = interval;
        Result.Opacity = 1 - TransparencyInput.Value / 100;
        Result.AlwaysOnTop = TopmostInput.IsChecked == true;
        Result.ManualMode = ManualInput.IsChecked == true;
        Result.ManualFiveHour = five; Result.ManualFable = fable; Result.ManualReset = reset;
        DialogResult = true;
    }

    private static bool Percent(string text, out double value) => double.TryParse(text, NumberStyles.Float,
        CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value >= 0 && value <= 100;
    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
    private async void Logout(object sender, RoutedEventArgs e)
    {
        LogoutButton.IsEnabled = false;
        try { await _logout(); ErrorText.Text = "연결을 해제하고 위젯 인증을 삭제했습니다. Claude Code 로그인은 유지됩니다."; }
        catch (Exception) { ErrorText.Text = "조회는 중지됐지만 인증 삭제 또는 상태 저장에 실패했습니다. 연결 해제를 다시 눌러 주세요."; }
        finally { LogoutButton.IsEnabled = true; }
    }
}
