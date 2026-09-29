using System.Windows;

namespace ClaudeUsageWidget;

public partial class App : Application
{
    private Mutex? _mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!e.Args.Contains("--smoke-test"))
        {
            _mutex = new Mutex(true, @"Local\ClaudeUsageWidget", out var created);
            if (!created) { MessageBox.Show("Claude 사용량 위젯이 이미 실행 중입니다."); Shutdown(); return; }
        }
        var window = new MainWindow(e.Args.Contains("--smoke-test"));
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
