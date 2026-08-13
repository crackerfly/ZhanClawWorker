using System.Threading;
using System.Windows;
using ZhanClawControl.Services;
using ZhanClawControl.Views;

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
        var wizard = new WizardWindow();
        Theme.Track(wizard);

        wizard.Completed += (_, success) =>
        {
            wizard.Close();

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
        var window = new MainWindow();
        Theme.Track(window);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
