using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Xml.Linq;

namespace ZhanClawControl.Services;

public enum TaskState { NotInstalled, Ready, Running, Disabled, Unknown }
public sealed record ScheduledTaskInspection(bool Exists, bool MatchesExpectedDefinition, string RunAsUser,
    string RawXml, IReadOnlyList<string> Issues, bool QueryFailed = false, string QueryError = "");

/// <summary>
/// Uses Task Scheduler COM, avoiding localized console output and mutable XML files. Cancellation is
/// checked before each COM mutation; a mutation already submitted is observed to completion so its
/// result is never reported ambiguously.
/// </summary>
public sealed class ScheduledTaskService
{
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int HResultFileNotFound = unchecked((int)0x80070002);
    private const int SchedEUnknownObject = unchecked((int)0x8004130F);

    public Task<TaskState> GetStateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(WithTask(task => (int)task.State switch
            {
                1 => TaskState.Disabled, 3 => TaskState.Ready, 4 => TaskState.Running, _ => TaskState.Unknown
            }));
        }
        catch (COMException ex) when (IsTaskNotFound(ex)) { return Task.FromResult(TaskState.NotInstalled); }
        catch { return Task.FromResult(TaskState.Unknown); }
    }

    public Task<ScheduledTaskInspection> InspectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string xml;
        try { xml = WithTask(task => (string)task.Xml); }
        catch (COMException ex) when (IsTaskNotFound(ex))
        {
            return Task.FromResult(new ScheduledTaskInspection(false, false, "", "", new[] { "计划任务不存在。" }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScheduledTaskInspection(true, false, "", "",
                new[] { "计划任务查询失败，无法证明其不存在。" }, true, ex.Message));
        }

        var issues = new List<string>();
        try
        {
            var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            var root = document.Root ?? throw new InvalidDataException("任务 XML 缺少根元素。");
            var ns = root.Name.Namespace;
            var principals = root.Element(ns + "Principals")?.Elements().ToList() ?? new();
            var principal = principals.Count == 1 && principals[0].Name == ns + "Principal" ? principals[0] : null;
            var runAsUser = Value(principal, ns, "UserId");
            if (principal is null) issues.Add("Principal 数量或类型不是精确的 1 个。");
            if (runAsUser.Length == 0) issues.Add("任务缺少 UserId。");
            Expect(Value(principal, ns, "LogonType"), "InteractiveToken", "LogonType", issues);
            Expect(Value(principal, ns, "RunLevel"), "LeastPrivilege", "RunLevel", issues);

            var triggers = root.Element(ns + "Triggers")?.Elements().ToList() ?? new();
            var logon = triggers.Count == 1 && triggers[0].Name == ns + "LogonTrigger" ? triggers[0] : null;
            if (logon is null) issues.Add("Triggers 必须只包含 1 个 LogonTrigger。");
            if (!SameAccount(runAsUser, Value(logon, ns, "UserId"))) issues.Add("Trigger UserId 与 Principal UserId 不一致。");
            Expect(Value(logon, ns, "Enabled"), "true", "LogonTrigger.Enabled", issues);

            var actionsNode = root.Element(ns + "Actions");
            var actions = actionsNode?.Elements().ToList() ?? new();
            var exec = actions.Count == 1 && actions[0].Name == ns + "Exec" ? actions[0] : null;
            if (exec is null) issues.Add("Actions 必须只包含 1 个 Exec。");
            if (!string.Equals(actionsNode?.Attribute("Context")?.Value, "Author", StringComparison.Ordinal)) issues.Add("Actions Context 不是 Author。");
            if (!PathsEqual(Value(exec, ns, "Command"), AppPaths.ControlExe)) issues.Add("Command 未精确指向安装目录后台宿主。");
            if (!string.Equals(Value(exec, ns, "Arguments"), AgentHost.Switch, StringComparison.Ordinal)) issues.Add("Arguments 不是精确的 --run-agent。");
            if (!PathsEqual(Value(exec, ns, "WorkingDirectory"), AppPaths.InstallRoot)) issues.Add("WorkingDirectory 不是安装目录。");

            var settings = root.Element(ns + "Settings");
            var enabledText = Value(settings, ns, "Enabled");
            if (!string.Equals(enabledText, "true", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(enabledText, "false", StringComparison.OrdinalIgnoreCase))
                issues.Add("Settings.Enabled 不是合法布尔值。");
            Expect(Value(settings, ns, "AllowStartOnDemand"), "true", "AllowStartOnDemand", issues);
            Expect(Value(settings, ns, "MultipleInstancesPolicy"), "IgnoreNew", "MultipleInstancesPolicy", issues);
            Expect(Value(settings, ns, "DisallowStartIfOnBatteries"), "false", "DisallowStartIfOnBatteries", issues);
            Expect(Value(settings, ns, "StopIfGoingOnBatteries"), "false", "StopIfGoingOnBatteries", issues);
            Expect(Value(settings, ns, "AllowHardTerminate"), "true", "AllowHardTerminate", issues);
            Expect(Value(settings, ns, "StartWhenAvailable"), "true", "StartWhenAvailable", issues);
            Expect(Value(settings, ns, "RunOnlyIfNetworkAvailable"), "false", "RunOnlyIfNetworkAvailable", issues);
            Expect(Value(settings, ns, "WakeToRun"), "false", "WakeToRun", issues);
            Expect(Value(settings, ns, "ExecutionTimeLimit"), "PT0S", "ExecutionTimeLimit", issues);
            Expect(Value(settings, ns, "Priority"), "7", "Priority", issues);
            Expect(Value(settings, ns, "Hidden"), "false", "Hidden", issues);
            Expect(Value(settings, ns, "RunOnlyIfIdle"), "false", "RunOnlyIfIdle", issues);
            Expect(Value(settings, ns, "DisallowStartOnRemoteAppSession"), "false", "DisallowStartOnRemoteAppSession", issues);
            Expect(Value(settings, ns, "UseUnifiedSchedulingEngine"), "true", "UseUnifiedSchedulingEngine", issues);
            var idle = settings?.Element(ns + "IdleSettings");
            Expect(Value(idle, ns, "StopOnIdleEnd"), "false", "IdleSettings.StopOnIdleEnd", issues);
            Expect(Value(idle, ns, "RestartOnIdle"), "false", "IdleSettings.RestartOnIdle", issues);
            var restart = settings?.Element(ns + "RestartOnFailure");
            Expect(Value(restart, ns, "Interval"), "PT1M", "RestartOnFailure.Interval", issues);
            Expect(Value(restart, ns, "Count"), "3", "RestartOnFailure.Count", issues);
            return Task.FromResult(new ScheduledTaskInspection(true, issues.Count == 0, runAsUser, xml, issues));
        }
        catch (Exception ex)
        {
            issues.Add($"任务 XML 无法解析：{ex.Message}");
            return Task.FromResult(new ScheduledTaskInspection(true, false, "", xml, issues));
        }
    }

    public async Task<ProcessResult> RegisterAsync(string runAsUser, CancellationToken ct = default)
    {
        var sid = RuntimeSecurityService.ResolveAccountSid(runAsUser).Value;
        var result = await RegisterXmlAsync(BuildTaskXml(sid), ct).ConfigureAwait(false);
        if (!result.Success) return result;
        var inspection = await InspectAsync(ct).ConfigureAwait(false);
        return inspection.MatchesExpectedDefinition ? result : Failure("任务已创建，但精确定义校验失败：" + string.Join("；", inspection.Issues));
    }

    public Task<ProcessResult> RegisterXmlAsync(string xml, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var userId = ReadPrincipalUserId(xml);
            WithFolder(folder =>
            {
                object? registered = null;
                try { registered = folder.RegisterTask(AppPaths.ScheduledTaskName, xml, TaskCreateOrUpdate, userId, null, TaskLogonInteractiveToken, null); }
                finally { ReleaseCom(registered); }
                return 0;
            });
            return Task.FromResult(Success("Task Scheduler COM registration completed."));
        }
        catch (Exception ex) { return Task.FromResult(Failure(ex.Message)); }
    }

    public async Task<ProcessResult> StartAsync(CancellationToken ct = default)
    {
        var inspection = await InspectAsync(ct).ConfigureAwait(false);
        if (inspection.QueryFailed || !inspection.Exists || !inspection.MatchesExpectedDefinition)
            return Failure("拒绝启动未通过精确定义校验的同名任务：" + string.Join("；",
                inspection.Issues.Append(inspection.QueryError).Where(x => !string.IsNullOrWhiteSpace(x))));
        try
        {
            ct.ThrowIfCancellationRequested();
            var initiallyEnabled = ReadTaskEnabled(inspection.RawXml);
            WithTask(task =>
            {
                object? running = null;
                try
                {
                    // A manual start is allowed while login autostart is disabled. Temporarily enable
                    // only for the COM Run submission, then restore the stored preference immediately.
                    if (!initiallyEnabled) task.Enabled = true;
                    running = task.Run(null);
                }
                finally
                {
                    ReleaseCom(running);
                    if (!initiallyEnabled) task.Enabled = false;
                }
                return 0;
            });
        }
        catch (Exception ex) { return Failure(ex.Message); }
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (IsExactProcessRunning("ZhanClawControl", AppPaths.ControlExe) || IsAgentProcessRunning()) return Success("Task started.");
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return Failure("任务运行命令已接受，但 10 秒内未出现本产品宿主或 Agent 进程。");
    }

    public async Task<ProcessResult> StopAsync(CancellationToken ct = default)
    {
        var errors = new List<string>();
        try { ct.ThrowIfCancellationRequested(); WithTask(task => { task.Stop(0); return 0; }); }
        catch (COMException ex) when (IsTaskNotFound(ex)) { }
        catch (Exception ex) { errors.Add("停止计划任务失败：" + ex.Message); }
        var kill = KillAgentProcesses();
        if (!kill.Success) errors.Add(kill.CombinedOutput);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && AnyProductProcessRunning()) { ct.ThrowIfCancellationRequested(); await Task.Delay(150, ct).ConfigureAwait(false); }
        if (AnyProductProcessRunning()) errors.Add("本产品 Agent/宿主进程仍在运行。");
        return errors.Count == 0 ? Success("Task and product processes stopped.") : Failure(string.Join(Environment.NewLine, errors));
    }

    public Task<ProcessResult> DeleteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try { WithFolder(folder => { folder.DeleteTask(AppPaths.ScheduledTaskName, 0); return 0; }); return Task.FromResult(Success("Task deleted.")); }
        catch (COMException ex) when (IsTaskNotFound(ex)) { return Task.FromResult(Success("Task was already absent.")); }
        catch (Exception ex) { return Task.FromResult(Failure(ex.Message)); }
    }

    public async Task<ProcessResult> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (enabled)
        {
            var inspection = await InspectAsync(ct).ConfigureAwait(false);
            if (inspection.QueryFailed || !inspection.Exists || !inspection.MatchesExpectedDefinition)
                return Failure("拒绝启用未通过精确定义校验的同名任务：" + string.Join("；", inspection.Issues));
        }
        try { WithTask(task => { task.Enabled = enabled; return 0; }); return Success("Task state updated."); }
        catch (Exception ex) { return Failure(ex.Message); }
    }

    public static bool IsAgentProcessRunning() => IsExactProcessRunning("p2p-agent", AppPaths.AgentExe);
    public static ProcessResult KillAgentProcesses()
    {
        var errors = new List<string>();
        KillExactProcesses("ZhanClawControl", AppPaths.ControlExe, errors); KillExactProcesses("p2p-agent", AppPaths.AgentExe, errors);
        return errors.Count == 0 ? Success("") : Failure(string.Join(Environment.NewLine, errors));
    }

    private static T WithTask<T>(Func<dynamic, T> action) => WithFolder(folder =>
    {
        object? task = null;
        try { task = folder.GetTask(AppPaths.ScheduledTaskName); return action((dynamic)task); }
        finally { ReleaseCom(task); }
    });
    private static T WithFolder<T>(Func<dynamic, T> action)
    {
        object? service = null; object? folder = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true) ?? throw new PlatformNotSupportedException("Task Scheduler COM unavailable.");
            service = Activator.CreateInstance(type) ?? throw new COMException("Cannot create Task Scheduler COM service.");
            ((dynamic)service).Connect(); folder = ((dynamic)service).GetFolder("\\"); return action((dynamic)folder);
        }
        finally { ReleaseCom(folder); ReleaseCom(service); }
    }
    private static void ReleaseCom(object? value) { if (value is not null && Marshal.IsComObject(value)) try { Marshal.FinalReleaseComObject(value); } catch { } }
    private static bool IsTaskNotFound(COMException ex) => ex.HResult is HResultFileNotFound or SchedEUnknownObject;
    private static string ReadPrincipalUserId(string xml)
    {
        var doc = XDocument.Parse(xml); var root = doc.Root ?? throw new InvalidDataException("任务 XML 缺少根元素。"); var ns = root.Name.Namespace;
        var users = root.Element(ns + "Principals")?.Elements(ns + "Principal").Select(p => Value(p, ns, "UserId")).Where(x => x.Length > 0).ToList() ?? new();
        if (users.Count != 1) throw new InvalidDataException("任务 XML 必须含唯一 Principal/UserId。"); return users[0];
    }
    public static bool ReadTaskEnabled(string xml)
    {
        var doc = XDocument.Parse(xml); var root = doc.Root ?? throw new InvalidDataException("任务 XML 缺少根元素。"); var ns = root.Name.Namespace;
        return Value(root.Element(ns + "Settings"), ns, "Enabled") switch
        {
            var value when string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) => true,
            var value when string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) => false,
            _ => throw new InvalidDataException("Settings.Enabled 不是合法布尔值。")
        };
    }
    private static string Value(XElement? parent, XNamespace ns, string name) => parent?.Element(ns + name)?.Value.Trim() ?? "";
    private static void Expect(string actual, string expected, string field, ICollection<string> issues) { if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) issues.Add($"{field} 不是 {expected}。"); }
    private static bool AnyProductProcessRunning() => IsExactProcessRunning("ZhanClawControl", AppPaths.ControlExe) || IsAgentProcessRunning();
    private static void KillExactProcesses(string name, string expectedPath, ICollection<string> errors)
    {
        foreach (var process in Process.GetProcessesByName(name)) try { if (process.Id != Environment.ProcessId && IsProcessAtPath(process, expectedPath)) { process.Kill(true); if (!process.WaitForExit(5_000)) errors.Add($"进程未在超时内退出：{expectedPath} pid={process.Id}"); } } catch (Exception ex) { errors.Add($"结束进程失败：{expectedPath} pid={process.Id}：{ex.Message}"); } finally { process.Dispose(); }
    }
    private static bool IsExactProcessRunning(string name, string expectedPath)
    {
        foreach (var process in Process.GetProcessesByName(name)) try { if (process.Id != Environment.ProcessId && IsProcessAtPath(process, expectedPath)) return true; } catch { } finally { process.Dispose(); }
        return false;
    }
    private static bool IsProcessAtPath(Process process, string path) => process.MainModule?.FileName is { } actual && PathsEqual(actual, path);
    private static bool PathsEqual(string left, string right) { try { return string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); } catch { return false; } }
    private static bool SameAccount(string left, string right) { if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true; try { return RuntimeSecurityService.ResolveAccountSid(left).Equals(RuntimeSecurityService.ResolveAccountSid(right)); } catch { return false; } }
    private static ProcessResult Success(string output) => new(0, output, "");
    private static ProcessResult Failure(string error) => new(-1, "", error);

    private static string BuildTaskXml(string runAsSid)
    {
        var user = SecurityElement.Escape(runAsSid) ?? runAsSid; var command = SecurityElement.Escape(AppPaths.ControlExe) ?? AppPaths.ControlExe;
        var args = SecurityElement.Escape(AgentHost.Switch) ?? AgentHost.Switch; var dir = SecurityElement.Escape(AppPaths.InstallRoot) ?? AppPaths.InstallRoot;
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Date>{DateTime.UtcNow.ToString("s", CultureInfo.InvariantCulture)}Z</Date><Author>ZhanClawControl</Author><Description>{AppInfo.ProductName} - 后台进程</Description><URI>\{AppPaths.ScheduledTaskName}</URI></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings><AllowStartOnDemand>true</AllowStartOnDemand><RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable><RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><WakeToRun>false</WakeToRun><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority><IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings><Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession><UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine></Settings>
  <Actions Context="Author"><Exec><Command>{command}</Command><Arguments>{args}</Arguments><WorkingDirectory>{dir}</WorkingDirectory></Exec></Actions>
</Task>
""";
    }
}
