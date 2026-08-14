using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using ZhanClawControl.Localization;
using ZhanClawControl.Infrastructure;
using ZhanClawControl.Services;
using ZhanClawControl.Views.Dialogs;

namespace ZhanClawControl;

public partial class App : Application
{
    private const string SingleInstanceMutexPrefix = @"Local\ZhanClawControl.SingleInstance";
    private const string MachineInstanceMutexName = @"Global\ZhanClawControl.MachineInstance.v1";
    private const string InteractiveUserSidSwitch = "--interactive-user-sid";
    private const string InteractiveUserLocaleSwitch = "--interactive-user-locale";

    private Mutex? _mutex;
    private Mutex? _machineMutex;
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
                             TryResolveCurrentSessionUserSid() ??
                             WindowsIdentity.GetCurrent().User?.Value;

        var interactiveLocale = ReadLocaleArgument(args, InteractiveUserLocaleSwitch) ??
                                TryResolveInteractiveUserCultureName(InteractiveUserSid) ??
                                CultureInfo.CurrentUICulture.Name;
        Localization.Initialize(interactiveLocale);
        // Theme must be available before any startup/UAC/single-instance prompt.
        // Initialize again after elevation is harmless and preserves the original
        // interactive user's preference rather than the credential account's.
        Theme.Initialize(InteractiveUserSid);

        // ---- GUI 模式 ----
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AsyncRelayCommand.UnhandledException += OnCommandUnhandledException;

        // 安装、注册计划任务、写 Program Files 与收紧 ACL 都需要管理员，
        // 清单是 asInvoker，因此这里自行提权重启。
        if (!IsElevated())
        {
            var relaunched = RelaunchElevated(InteractiveUserSid, Localization.SystemCultureName);
            Shutdown(relaunched ? 0 : 10);
            return;
        }

        // The per-session mutex prevents duplicate windows for one user, while
        // the machine-wide mutex also closes the credential-UAC gap: two
        // elevated identities must never mutate the same machine installation.
        try
        {
            _machineMutex = new Mutex(true, MachineInstanceMutexName, out var createdMachineMutex);
            if (!createdMachineMutex)
            {
                _machineMutex.Dispose();
                _machineMutex = null;
                ShowAnotherInstanceAndExit();
                return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            ShowAnotherInstanceAndExit();
            return;
        }

        var mutexName = $"{SingleInstanceMutexPrefix}.{InteractiveUserSid}.{Process.GetCurrentProcess().SessionId}";
        _mutex = new Mutex(true, mutexName, out var createdNew);
        if (!createdNew)
        {
            ShowAnotherInstanceAndExit();
            return;
        }

        // Recovery artifacts outrank the ordinary installed/not-installed
        // route. Otherwise a power loss after deployment removal can strand
        // the user in the first-install wizard with no safe recovery entry.
        if (InstallerService.HasInterruptedUninstallArtifacts)
        {
            try
            {
                InstallerService.CleanupInterruptedUninstallTombstones();
                if (Directory.Exists(AppPaths.UninstallRecoveryRoot))
                {
                    ShowInterruptedUninstallRecovery(InstallerService.GetInterruptedUninstallRecovery());
                    return;
                }

                if (InstallerService.HasInterruptedUninstallArtifacts)
                {
                    throw new InvalidDataException(
                        "卸载恢复工件仍然存在，但没有可读取的受保护活动状态目录。");
                }
            }
            catch (Exception ex)
            {
                AppDialog.Show(
                    Localization.Format("DialogInterruptedUninstallInvalid", ex.Message),
                    Localization.Text("DialogInterruptedUninstallTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                ShowMainWindow();
                return;
            }
        }

        if (!InstallerService.IsInstalled)
        {
            ShowWizard();
        }
        else
        {
            ShowMainWindow();
        }
    }

    private void ShowAnotherInstanceAndExit()
    {
        AppDialog.Show(
            Localization.Format("AppAlreadyRunning", Localization.Text("ProductName")),
            Localization.Text("ProductName"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        Shutdown();
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
            AppDialog.Show(
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
            AppDialog.Show(
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

    private async void ShowInterruptedUninstallRecovery(
        InstallerService.InterruptedUninstallRecovery recovery)
    {
        var messageArguments = new object[]
        {
            Localization.Text(recovery.RemoveData
                ? "InterruptedUninstallDeleteDataIntent"
                : "InterruptedUninstallKeepDataIntent"),
            recovery.Phase
        };

        string action;
        if (recovery.CanContinue && recovery.CanRollback)
        {
            action = AppDialog.ShowActionsFormat(
                "DialogInterruptedUninstallPending", messageArguments,
                "DialogInterruptedUninstallTitle",
                new[]
                {
                    new AppDialogAction("Continue", "DialogActionCompleteInterruptedUninstall", AppDialogActionStyle.Danger),
                    new AppDialogAction("Rollback", "DialogActionRollbackInterruptedUninstall", AppDialogActionStyle.Primary),
                    new AppDialogAction("Exit", "DialogActionExit", AppDialogActionStyle.Secondary, IsDefault: true, IsCancel: true)
                },
                MessageBoxImage.Warning);
        }
        else if (!recovery.CanContinue)
        {
            action = AppDialog.ShowActionsFormat(
                "DialogInterruptedUninstallPending", messageArguments,
                "DialogInterruptedUninstallTitle",
                new[]
                {
                    new AppDialogAction("Rollback", "DialogActionRollbackInterruptedUninstall", AppDialogActionStyle.Primary),
                    new AppDialogAction("Exit", "DialogActionExit", AppDialogActionStyle.Secondary, IsDefault: true, IsCancel: true)
                },
                MessageBoxImage.Warning);
        }
        else
        {
            action = AppDialog.ShowActionsFormat(
                "DialogInterruptedUninstallPending", messageArguments,
                "DialogInterruptedUninstallTitle",
                new[]
                {
                    new AppDialogAction("Continue", "DialogActionCompleteInterruptedUninstall", AppDialogActionStyle.Danger),
                    new AppDialogAction("Exit", "DialogActionExit", AppDialogActionStyle.Secondary, IsDefault: true, IsCancel: true)
                },
                MessageBoxImage.Warning);
        }

        if (action == "Exit")
        {
            Shutdown();
            return;
        }

        try
        {
            var installer = new InstallerService();
            if (action == "Rollback")
            {
                await installer.RollbackInterruptedUninstallAsync().ConfigureAwait(true);
                AppDialog.ShowResource(
                    "DialogInterruptedUninstallRollbackSucceeded",
                    "DialogInterruptedUninstallTitle",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                ShowMainWindow();
                return;
            }

            var steps = await installer.ResumeInterruptedUninstallAsync().ConfigureAwait(true);
            AppDialog.ShowResource(
                steps.Any(step => step.Success && step.RequiresDeferredCleanup)
                    ? "DialogUninstalledDeferred"
                    : "DialogUninstalled",
                "ProductName",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
        }
        catch (Exception ex)
        {
            AppDialog.Show(
                Localization.Format("DialogInterruptedUninstallFailed", ex.Message),
                Localization.Text("DialogInterruptedUninstallTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ShowMainWindow();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AsyncRelayCommand.UnhandledException -= OnCommandUnhandledException;
        _hostCts?.Cancel();
        _hostCts?.Dispose();
        _mutex?.Dispose();
        _machineMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Environment.ExitCode = 10;
    }

    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // A dispatcher exception is isolated to the current UI callback. Mark it
        // handled and leave repair/settings access available. Truly fatal CLR
        // exceptions do not reliably reach this event and remain process-fatal.
        e.Handled = true;
        ShowRecoverableException(e.Exception);
    }

    private void OnCommandUnhandledException(object? sender, Exception e) =>
        ShowRecoverableException(e);

    private static void ShowRecoverableException(Exception exception)
    {
        AppDialog.Show(
            Localization.Format("AppRecoverableError", exception.Message),
            Localization.Text("ProductName"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // This event can be raised much later by GC for a task whose owning
        // operation has already completed. It is not evidence that current UI
        // state is unsafe and must never silently close the user's window.
        e.SetObserved();
    }

    private static string? TryResolveCurrentSessionUserSid()
    {
        IntPtr userNameBuffer = IntPtr.Zero;
        IntPtr domainBuffer = IntPtr.Zero;
        try
        {
            var sessionId = Process.GetCurrentProcess().SessionId;
            if (!WTSQuerySessionInformation(
                    IntPtr.Zero, sessionId, WtsInfoClass.UserName,
                    out userNameBuffer, out _) || userNameBuffer == IntPtr.Zero)
                return null;
            if (!WTSQuerySessionInformation(
                    IntPtr.Zero, sessionId, WtsInfoClass.DomainName,
                    out domainBuffer, out _) || domainBuffer == IntPtr.Zero)
                return null;

            var user = Marshal.PtrToStringUni(userNameBuffer)?.Trim();
            var domain = Marshal.PtrToStringUni(domainBuffer)?.Trim();
            if (string.IsNullOrWhiteSpace(user)) return null;

            var account = string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
            var sid = (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
            return sid.IsAccountSid() ? sid.Value : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (userNameBuffer != IntPtr.Zero) WTSFreeMemory(userNameBuffer);
            if (domainBuffer != IntPtr.Zero) WTSFreeMemory(domainBuffer);
        }
    }

    private static string? TryResolveInteractiveUserCultureName(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) return null;
        try
        {
            using var profile = Registry.Users.OpenSubKey($@"{sid}\Control Panel\International\User Profile");
            if (profile?.GetValue("Languages") is string[] { Length: > 0 } languages)
            {
                foreach (var language in languages)
                {
                    if (string.IsNullOrWhiteSpace(language)) continue;
                    try { return CultureInfo.GetCultureInfo(language).Name; }
                    catch (CultureNotFoundException) { }
                }
            }

            using var key = Registry.Users.OpenSubKey($@"{sid}\Control Panel\International");
            var localeName = key?.GetValue("LocaleName") as string;
            if (string.IsNullOrWhiteSpace(localeName)) return null;
            return CultureInfo.GetCultureInfo(localeName).Name;
        }
        catch
        {
            return null;
        }
    }

    private enum WtsInfoClass
    {
        UserName = 5,
        DomainName = 7
    }

    [DllImport("Wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr serverHandle,
        int sessionId,
        WtsInfoClass infoClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("Wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

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
