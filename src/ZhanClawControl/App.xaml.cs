using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using ZhanClawControl.Localization;
using ZhanClawControl.Services;

namespace ZhanClawControl;

public partial class App : Application
{
    private const string SingleInstanceMutexPrefix = @"Local\ZhanClawControl.SingleInstance";
    private const string InteractiveUserSidSwitch = "--interactive-user-sid";
    private const string InteractiveUserLocaleSwitch = "--interactive-user-locale";

    private Mutex? _mutex;
    private CancellationTokenSource? _hostCts;

    public static ThemeService Theme { get; } = new();
    public static LocalizationService Localization { get; } = new();
    public static string? InteractiveUserSid { get; private set; }

    public static string InteractiveUserName
    {
        get
        {
            try
            {
                return InteractiveUserSid is null
                    ? InstallerService.CurrentUserName
                    : ((NTAccount)new SecurityIdentifier(InteractiveUserSid)
                        .Translate(typeof(NTAccount))).Value;
            }
            catch
            {
                // Task Scheduler accepts a canonical SID. Never silently switch
                // a credential-elevated launch to the administrator account just
                // because SID -> NTAccount translation is unavailable.
                return InteractiveUserSid ?? InstallerService.CurrentUserName;
            }
        }
    }

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

        InteractiveUserSid = ReadArgument(args, InteractiveUserSidSwitch) ??
                             WindowsIdentity.GetCurrent().User?.Value;

        var interactiveLocale = ReadLocaleArgument(args, InteractiveUserLocaleSwitch) ??
                                CultureInfo.CurrentUICulture.Name;
        Localization.Initialize(interactiveLocale);

        // ---- GUI 模式 ----
        DispatcherUnhandledException += (_, dispatcherArgs) =>
        {
            MessageBox.Show(
                Localization.Format("AppUnhandled", dispatcherArgs.Exception.Message),
                Localization.Text("ProductName"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            dispatcherArgs.Handled = true;
            Shutdown(10);
        };
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 安装、注册计划任务、写 Program Files 与收紧 ACL 都需要管理员，
        // 清单是 asInvoker，因此这里自行提权重启。
        if (!IsElevated())
        {
            var relaunched = RelaunchElevated(InteractiveUserSid, Localization.SystemCultureName);
            Shutdown(relaunched ? 0 : 10);
            return;
        }

        var mutexName = $"{SingleInstanceMutexPrefix}.{InteractiveUserSid}.{Process.GetCurrentProcess().SessionId}";
        _mutex = new Mutex(true, mutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                Localization.Format("AppAlreadyRunning", Localization.Text("ProductName")),
                Localization.Text("ProductName"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Theme.Initialize(InteractiveUserSid);

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
                var exitCode = await AgentHost.RunAsync(_hostCts.Token).ConfigureAwait(false);
                Dispatcher.Invoke(() => Shutdown(exitCode));
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() => Shutdown(5));
            }
            catch
            {
                Dispatcher.Invoke(() => Shutdown(10));
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

    private static bool RelaunchElevated(string? interactiveUserSid, string? interactiveUserLocale)
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(
                Localization.Text("AppPathMissing"),
                Localization.Text("ProductName"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = "runas"
            };
            if (!string.IsNullOrWhiteSpace(interactiveUserSid))
            {
                startInfo.ArgumentList.Add(InteractiveUserSidSwitch);
                startInfo.ArgumentList.Add(interactiveUserSid);
            }
            if (!string.IsNullOrWhiteSpace(interactiveUserLocale))
            {
                startInfo.ArgumentList.Add(InteractiveUserLocaleSwitch);
                startInfo.ArgumentList.Add(interactiveUserLocale);
            }

            return Process.Start(startInfo) is not null;
        }
        catch (Exception ex)
        {
            // 用户在 UAC 弹窗点了「否」会走到这里
            MessageBox.Show(
                Localization.Format("AppNeedsAdmin", ex.Message),
                Localization.Text("ProductName"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
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
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        _hostCts?.Cancel();
        _hostCts?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Environment.ExitCode = 10;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        Environment.ExitCode = 10;
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(() => Shutdown(10));
    }

    private static string? ReadArgument(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                var value = args[index + 1];
                try
                {
                    return new SecurityIdentifier(value).Value;
                }
                catch
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static string? ReadLocaleArgument(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var culture = CultureInfo.GetCultureInfo(args[index + 1]);
                if (culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                {
                    return culture.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
                           culture.Name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) ||
                           culture.Name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) ||
                           culture.Name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase)
                        ? LocalizationService.TraditionalChinese
                        : LocalizationService.SimplifiedChinese;
                }
                return LocalizationService.English;
            }
            catch (CultureNotFoundException)
            {
                return null;
            }
        }
        return null;
    }
}
