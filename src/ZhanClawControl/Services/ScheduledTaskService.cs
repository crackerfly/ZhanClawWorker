using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace ZhanClawControl.Services;

public enum TaskState
{
    NotInstalled,
    Ready,
    Running,
    Disabled,
    Unknown
}

/// <summary>
/// 通过 schtasks.exe 管理名为 "P2P Agent" 的登录时计划任务。
/// 不引入 NuGet 依赖；任务定义用 XML 提交，可完整控制 LogonType / RunLevel / 重启策略。
/// </summary>
public sealed class ScheduledTaskService
{
    private static string SchTasks => ProcessRunner.SystemPath("schtasks.exe");

    public async Task<TaskState> GetStateAsync(CancellationToken ct = default)
    {
        var result = await ProcessRunner
            .RunAsync(SchTasks, new[] { "/Query", "/TN", AppPaths.ScheduledTaskName, "/FO", "LIST" }, 20_000, ct)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return TaskState.NotInstalled;
        }

        var text = result.StdOut;

        // schtasks 输出随系统语言变化，因此同时匹配中英文状态词
        if (Contains(text, "Running") || Contains(text, "正在运行"))
        {
            return TaskState.Running;
        }

        if (Contains(text, "Disabled") || Contains(text, "已禁用"))
        {
            return TaskState.Disabled;
        }

        if (Contains(text, "Ready") || Contains(text, "就绪") || Contains(text, "准备就绪"))
        {
            return TaskState.Ready;
        }

        return TaskState.Unknown;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Agent 进程是否真的在跑（计划任务状态不足以判断）。</summary>
    public static bool IsAgentProcessRunning()
    {
        try
        {
            return Process.GetProcessesByName("p2p-agent").Any();
        }
        catch
        {
            return false;
        }
    }

    public async Task<ProcessResult> RegisterAsync(string runAsUser, CancellationToken ct = default)
    {
        var xml = BuildTaskXml(runAsUser);
        var tempPath = Path.Combine(Path.GetTempPath(), $"zhanclaw-task-{Guid.NewGuid():N}.xml");

        // schtasks /XML 要求 UTF-16 LE with BOM
        await File.WriteAllTextAsync(tempPath, xml, new UnicodeEncoding(false, true), ct).ConfigureAwait(false);

        try
        {
            return await ProcessRunner.RunAsync(
                SchTasks,
                new[] { "/Create", "/TN", AppPaths.ScheduledTaskName, "/XML", tempPath, "/F" },
                60_000,
                ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // 忽略
            }
        }
    }

    public Task<ProcessResult> StartAsync(CancellationToken ct = default) =>
        ProcessRunner.RunAsync(SchTasks, new[] { "/Run", "/TN", AppPaths.ScheduledTaskName }, 30_000, ct);

    public async Task<ProcessResult> StopAsync(CancellationToken ct = default)
    {
        var result = await ProcessRunner
            .RunAsync(SchTasks, new[] { "/End", "/TN", AppPaths.ScheduledTaskName }, 30_000, ct)
            .ConfigureAwait(false);

        // /End 只结束任务实例；启动器是 cmd.exe，Agent 可能成为孤儿，需要显式收尾
        KillAgentProcesses();
        return result;
    }

    public Task<ProcessResult> DeleteAsync(CancellationToken ct = default) =>
        ProcessRunner.RunAsync(SchTasks, new[] { "/Delete", "/TN", AppPaths.ScheduledTaskName, "/F" }, 30_000, ct);

    public Task<ProcessResult> SetEnabledAsync(bool enabled, CancellationToken ct = default) =>
        ProcessRunner.RunAsync(
            SchTasks,
            new[] { "/Change", "/TN", AppPaths.ScheduledTaskName, enabled ? "/ENABLE" : "/DISABLE" },
            30_000,
            ct);

    /// <summary>
    /// 结束 Agent 与宿主进程。schtasks /End 只结束任务实例，
    /// 宿主被终止后 p2p-agent 可能成为孤儿，因此显式收尾。
    /// </summary>
    public static void KillAgentProcesses()
    {
        KillHostProcesses();

        foreach (var process in Process.GetProcessesByName("p2p-agent"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
            catch
            {
                // 权限不足或已退出
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// 任务执行的是本程序的 --run-agent 宿主模式，而不是直接执行 p2p-agent.exe。
    /// p2p-agent.exe 是 CONSOLE 子系统程序，直接由计划任务启动会弹出黑窗；
    /// 本程序是 WinExe，以 CreateNoWindow 拉起 Agent 既无窗口，又能捕获其 stdout/stderr 写入日志。
    ///
    /// 注意：taskSettingsType 的子元素顺序由 XSD 的 sequence 约束，
    /// 顺序不对会被 schtasks /XML 拒绝，因此下面的 Settings 严格按 schema 顺序排列。
    /// </summary>
    /// <summary>
    /// 结束以 --run-agent 启动的宿主实例。
    ///
    /// 判定依据是可执行文件路径等于安装目录下的副本：单实例互斥量保证 GUI 只有一个，
    /// 即当前进程；因此路径匹配且非当前进程的，只可能是计划任务拉起的宿主。
    /// 不使用 WMI 读命令行，避免引入 System.Management 包依赖。
    /// </summary>
    private static void KillHostProcesses()
    {
        var self = Environment.ProcessId;

        foreach (var process in Process.GetProcessesByName("ZhanClawControl"))
        {
            try
            {
                if (process.Id == self)
                {
                    continue;
                }

                var path = process.MainModule?.FileName;
                if (path is null ||
                    !string.Equals(path, AppPaths.ControlExe, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
            catch
            {
                // 权限不足、已退出，或无法读取模块信息
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static string BuildTaskXml(string runAsUser)
    {
        var user = SecurityElement.Escape(runAsUser) ?? runAsUser;
        var command = SecurityElement.Escape(AppPaths.ControlExe) ?? AppPaths.ControlExe;
        var arguments = SecurityElement.Escape(AgentHost.Switch) ?? AgentHost.Switch;
        // 与官方安装脚本一致：工作目录取程序目录
        var workingDir = SecurityElement.Escape(AppPaths.InstallRoot) ?? AppPaths.InstallRoot;
        var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Date>{now}</Date>
    <Author>ZhanClawControl</Author>
    <Description>{AppInfo.ProductName} - 后台进程</Description>
    <URI>\{AppPaths.ScheduledTaskName}</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <RestartOnFailure>
      <Interval>PT1M</Interval>
      <Count>3</Count>
    </RestartOnFailure>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{command}</Command>
      <Arguments>{arguments}</Arguments>
      <WorkingDirectory>{workingDir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
    }
}
