using System.Threading;
using System.Windows;
using ZhanClawControl.Services;

namespace ZhanClawControl;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\ZhanClawControl.SingleInstance";

    private Mutex? _mutex;

    public static ThemeService Theme { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                $"{AppInfo.ProductName}已在运行。\n\n请从系统托盘打开窗口。",
                AppInfo.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"发生未处理的错误：\n\n{args.Exception.Message}",
                AppInfo.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        Theme.Initialize();

        if (!InstallerService.IsInstalled)
        {
            ShowWizard();
        }
        else
        {
            ShowMainWindow();
        }
    }

    private void ShowWizard()
    {
        var wizard = new Views.WizardWindow();
        Theme.Track(wizard);

        // Completed 在窗口 Closed 之后触发，这里不需要（也不能）再调用 Close()
        wizard.Completed += (_, success) =>
        {
            if (success)
            {
                ShowMainWindow();
            }
            else
            {
                Shutdown();
            }
        };

        wizard.Show();
    }

    private void ShowMainWindow()
    {
        var window = new Views.MainWindow();
        Theme.Track(window);
        base.MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
