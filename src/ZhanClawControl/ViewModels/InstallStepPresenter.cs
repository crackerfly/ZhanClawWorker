using System.IO;
using ZhanClawControl.Services;

namespace ZhanClawControl.ViewModels;

/// <summary>
/// Localized user-facing step plus the unmodified service detail. TechnicalDetail
/// is deliberately not bound by the normal wizard/repair/uninstall UI; it remains
/// available to a future diagnostics or expandable technical-details surface.
/// </summary>
public sealed record InstallStepDisplay(
    string Title,
    bool Success,
    string Detail,
    string TechnicalDetail = "",
    string ErrorCode = "");

internal static class InstallStepPresenter
{
    private sealed record StepPresentation(string TitleKey, string ErrorCode, string FailureKey);

    private static readonly IReadOnlyDictionary<string, StepPresentation> Presentations =
        new Dictionary<string, StepPresentation>(StringComparer.Ordinal)
        {
            ["读取现有计划任务"] = new("InstallStepReadTask", "ZC-INS-TASK", "InstallFailureTask"),
            ["确定运行账户"] = new("InstallStepAccount", "ZC-INS-ACCOUNT", "InstallFailureAccount"),
            ["校验目录与 ACL"] = new("InstallStepAcl", "ZC-INS-ACL", "InstallFailureAcl"),
            ["创建并保护安装目录"] = new("InstallStepDirectories", "ZC-INS-ACL", "InstallFailureAcl"),
            ["验证安装载荷"] = new("InstallStepPayload", "ZC-INS-PAYLOAD", "InstallFailurePayload"),
            ["停止后台进程"] = new("InstallStepStopBackground", "ZC-INS-STOP", "InstallFailureStop"),
            ["停止已有 Agent 实例"] = new("InstallStepStopBackground", "ZC-INS-STOP", "InstallFailureStop"),
            ["停止 Agent"] = new("InstallStepStopAgent", "ZC-INS-STOP", "InstallFailureStop"),
            ["更新程序文件"] = new("InstallStepDeploy", "ZC-INS-FILES", "InstallFailureFiles"),
            ["部署程序文件"] = new("InstallStepDeploy", "ZC-INS-FILES", "InstallFailureFiles"),
            ["移除程序文件"] = new("InstallStepRemoveFiles", "ZC-INS-FILES", "InstallFailureFiles"),
            ["写入 swarm.key"] = new("InstallStepSwarmKey", "ZC-INS-KEY", "InstallFailureKey"),
            ["写入 agent-config.json"] = new("InstallStepConfig", "ZC-INS-CONFIG", "InstallFailureConfig"),
            ["重建开机自启任务"] = new("InstallStepSignInTask", "ZC-INS-TASK", "InstallFailureTask"),
            ["注册开机自启任务"] = new("InstallStepSignInTask", "ZC-INS-TASK", "InstallFailureTask"),
            ["启动并验证 Agent"] = new("InstallStepStartVerify", "ZC-INS-START", "InstallFailureStart"),
            ["清理回滚备份"] = new("InstallStepCleanupRollback", "ZC-INS-CLEANUP", "InstallFailureCleanup"),
            ["修复安装"] = new("InstallStepRepair", "ZC-INS-REPAIR", "InstallFailureGeneric"),
            ["安装中断"] = new("WizardInstallInterrupted", "ZC-INS-INTERRUPTED", "InstallFailureGeneric"),
            ["回滚"] = new("InstallStepRollback", "ZC-INS-ROLLBACK", "InstallFailureRollback"),
            ["创建回滚点"] = new("InstallStepCreateRollback", "ZC-INS-ROLLBACK", "InstallFailureRollback"),
            ["清理回滚点"] = new("InstallStepCleanupRollbackPoint", "ZC-INS-CLEANUP", "InstallFailureCleanup"),
            ["卸载回滚"] = new("InstallStepUninstallRollback", "ZC-INS-ROLLBACK", "InstallFailureRollback"),
            ["隔离计划任务"] = new("InstallStepIsolateTask", "ZC-INS-TASK", "InstallFailureTask"),
            ["隔离运行数据"] = new("InstallStepIsolateData", "ZC-INS-DATA", "InstallFailureFiles"),
            ["清理隔离数据"] = new("InstallStepCleanupIsolatedData", "ZC-INS-CLEANUP", "InstallFailureCleanup"),
            ["删除计划任务"] = new("InstallStepDeleteTask", "ZC-INS-TASK", "InstallFailureTask"),
            ["删除程序文件"] = new("InstallStepDeleteFiles", "ZC-INS-FILES", "InstallFailureFiles"),
            ["删除运行数据"] = new("InstallStepDeleteData", "ZC-INS-DATA", "InstallFailureFiles"),
            ["保留运行数据"] = new("InstallStepKeepData", "ZC-INS-DATA", "InstallFailureFiles"),
            ["安排重启后清理"] = new("InstallStepDeferredCleanup", "ZC-INS-CLEANUP", "InstallFailureCleanup"),
            ["安排退出后清理"] = new("InstallStepDeferredCleanup", "ZC-INS-CLEANUP", "InstallFailureCleanup")
        };

    public static InstallStepDisplay Present(InstallStep step)
    {
        var known = Presentations.TryGetValue(step.Title, out var presentation);
        var title = known
            ? App.Localization.Text(presentation!.TitleKey)
            : App.Localization.Text("InstallStepOther");

        if (step.Success)
        {
            return new InstallStepDisplay(
                title,
                true,
                App.Localization.Text("InstallStepCompleted"),
                step.Detail);
        }

        var errorCode = known ? presentation!.ErrorCode : ClassifyUnknownFailure(step.Detail);
        var failureKey = known ? presentation!.FailureKey : FailureKeyForCode(errorCode);
        var localizedFailure = App.Localization.Text(failureKey);
        var summary = App.Localization.Format("InstallStepFailureSummary", localizedFailure, errorCode);
        if (TryGetProtectedResidualPath(step.Title, step.Detail, out var residualPath))
            summary = App.Localization.Format("InstallStepResidualPath", summary, residualPath);
        return new InstallStepDisplay(
            title,
            false,
            summary,
            step.Detail,
            errorCode);
    }

    private static string ClassifyUnknownFailure(string detail)
    {
        if (ContainsAny(detail, "swarm.key", "private-network key")) return "ZC-INS-KEY";
        if (ContainsAny(detail, "agent-config", "allowed_peers", "bootstrap", "rendezvous")) return "ZC-INS-CONFIG";
        if (ContainsAny(detail, "SHA-256", "Authenticode", "manifest", "载荷", "哈希")) return "ZC-INS-PAYLOAD";
        if (ContainsAny(detail, "Task Scheduler", "计划任务", "LogonTrigger")) return "ZC-INS-TASK";
        if (ContainsAny(detail, "ACL", "DACL", "权限", "access denied", "Unauthorized")) return "ZC-INS-ACL";
        if (ContainsAny(detail, "回滚", "rollback")) return "ZC-INS-ROLLBACK";
        if (ContainsAny(detail, "启动", "ready", "health", "API")) return "ZC-INS-START";
        if (ContainsAny(detail, "停止", "terminate", "kill")) return "ZC-INS-STOP";
        if (ContainsAny(detail, "文件", "目录", "path", "file", "directory")) return "ZC-INS-FILES";
        return "ZC-INS-UNKNOWN";
    }

    private static string FailureKeyForCode(string code) => code switch
    {
        "ZC-INS-KEY" => "InstallFailureKey",
        "ZC-INS-CONFIG" => "InstallFailureConfig",
        "ZC-INS-PAYLOAD" => "InstallFailurePayload",
        "ZC-INS-TASK" => "InstallFailureTask",
        "ZC-INS-ACL" => "InstallFailureAcl",
        "ZC-INS-ROLLBACK" => "InstallFailureRollback",
        "ZC-INS-START" => "InstallFailureStart",
        "ZC-INS-STOP" => "InstallFailureStop",
        "ZC-INS-FILES" => "InstallFailureFiles",
        _ => "InstallFailureGeneric"
    };

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetProtectedResidualPath(string title, string detail, out string path)
    {
        path = "";
        if (title is not ("清理隔离数据" or "清理回滚点")) return false;

        var marker = title == "清理隔离数据" ? "安全隔离数据仍保留：" : "受保护回滚点仍保留：";
        if (!detail.StartsWith(marker, StringComparison.Ordinal)) return false;
        var candidate = detail[marker.Length..].Split('；', 2)[0].Trim();
        if (!Path.IsPathFullyQualified(candidate)) return false;
        path = candidate;
        return true;
    }
}
