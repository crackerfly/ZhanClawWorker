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

    public static void KillAgentProcesses()
    {
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
    /// 任务执行的是 run-agent.cmd 而不是 p2p-agent.exe 本身，
    /// 目的是把 stdout/stderr 重定向到 logs\agent.log —— 官方安装脚本直接执行 exe，没有日志留存。
    /// </summary>
    private static string BuildTaskXml(string runAsUser)
    {
        var user = SecurityElement.Escape(runAsUser) ?? runAsUser;
        var command = SecurityElement.Escape(AppPaths.LauncherCmd) ?? AppPaths.LauncherCmd;
        var workingDir = SecurityElement.Escape(AppPaths.DataRoot) ?? AppPaths.DataRoot;
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
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <RestartOnFailure>
      <Interval>PT1M</Interval>
      <Count>3</Count>
    </RestartOnFailure>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{command}</Command>
      <WorkingDirectory>{workingDir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
    }
}
