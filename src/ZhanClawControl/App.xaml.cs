using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using ZhanClawControl.Services;

namespace ZhanClawControl;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\ZhanClawControl.SingleInstance";

    private Mutex? _mutex;
    private CancellationTokenSource? _hostCts;

    public static ThemeService Theme { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = Environment.GetCommandLineArgs();

        // ---- 宿主模式：由计划任务调用，不创建任何窗口，也不需要管理员权限 ----
        if (AgentHost.IsHostMode(args))
        {
            StartAgentHost();
            return;
        }

        // ---- GUI 模式 ----
        DispatcherUnhandledException += (_, dispatcherArgs) =>
        {
            MessageBox.Show(
                $"发生未处理的错误：\n\n{dispatcherArgs.Exception.Message}",
                AppInfo.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            dispatcherArgs.Handled = true;
        };

        // 安装、注册计划任务、写 Program Files 与收紧 ACL 都需要管理员，
        // 清单是 asInvoker，因此这里自行提权重启。
        if (!IsElevated())
        {
            RelaunchElevated();
            Shutdown();
            return;
        }

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

    private void StartAgentHost()
    {
        _hostCts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            try
            {
                await AgentHost.RunAsync(_hostCts.Token).ConfigureAwait(false);
            }
            finally
            {
                // Agent 退出后宿主进程随之结束，计划任务据此判定任务已结束
                Dispatcher.Invoke(Shutdown);
            }
        });
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void RelaunchElevated()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(
                "无法定位程序自身路径，请以管理员身份手动运行。",
                AppInfo.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Exception ex)
        {
            // 用户在 UAC 弹窗点了「否」会走到这里
            MessageBox.Show(
                "本程序需要管理员权限才能安装和管理被控端。\n\n" +
                $"提权失败：{ex.Message}\n\n请右键选择「以管理员身份运行」。",
                AppInfo.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
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
        _hostCts?.Cancel();
        _hostCts?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
