#nullable disable warnings
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ZhanClawControl.Services;

namespace ZhanClawControl.ViewModels;

internal static class InstallStepPresenter
{
	private sealed record StepPresentation(string TitleKey, string ErrorCode, string FailureKey);

	private static readonly IReadOnlyDictionary<string, StepPresentation> Presentations = new Dictionary<string, StepPresentation>(StringComparer.Ordinal)
	{
		["读取现有计划任务"] = new StepPresentation("InstallStepReadTask", "ZC-INS-TASK", "InstallFailureTask"),
		["确定运行账户"] = new StepPresentation("InstallStepAccount", "ZC-INS-ACCOUNT", "InstallFailureAccount"),
		["校验目录与 ACL"] = new StepPresentation("InstallStepAcl", "ZC-INS-ACL", "InstallFailureAcl"),
		["创建并保护安装目录"] = new StepPresentation("InstallStepDirectories", "ZC-INS-ACL", "InstallFailureAcl"),
		["验证安装载荷"] = new StepPresentation("InstallStepPayload", "ZC-INS-PAYLOAD", "InstallFailurePayload"),
		["停止后台进程"] = new StepPresentation("InstallStepStopBackground", "ZC-INS-STOP", "InstallFailureStop"),
		["停止已有 Agent 实例"] = new StepPresentation("InstallStepStopBackground", "ZC-INS-STOP", "InstallFailureStop"),
		["停止 Agent"] = new StepPresentation("InstallStepStopAgent", "ZC-INS-STOP", "InstallFailureStop"),
		["更新程序文件"] = new StepPresentation("InstallStepDeploy", "ZC-INS-FILES", "InstallFailureFiles"),
		["部署程序文件"] = new StepPresentation("InstallStepDeploy", "ZC-INS-FILES", "InstallFailureFiles"),
		["移除程序文件"] = new StepPresentation("InstallStepRemoveFiles", "ZC-INS-FILES", "InstallFailureFiles"),
		["写入 swarm.key"] = new StepPresentation("InstallStepSwarmKey", "ZC-INS-KEY", "InstallFailureKey"),
		["写入 agent-config.json"] = new StepPresentation("InstallStepConfig", "ZC-INS-CONFIG", "InstallFailureConfig"),
		["重建 swarm.key"] = new StepPresentation("InstallStepRebuildSwarmKey", "ZC-INS-KEY", "InstallFailureKey"),
		["重建 agent-config.json"] = new StepPresentation("InstallStepRebuildConfig", "ZC-INS-CONFIG", "InstallFailureConfig"),
		["重建开机自启任务"] = new StepPresentation("InstallStepSignInTask", "ZC-INS-TASK", "InstallFailureTask"),
		["注册开机自启任务"] = new StepPresentation("InstallStepSignInTask", "ZC-INS-TASK", "InstallFailureTask"),
		["启动并验证 Agent"] = new StepPresentation("InstallStepStartVerify", "ZC-INS-START", "InstallFailureStart"),
		["清理回滚备份"] = new StepPresentation("InstallStepCleanupRollback", "ZC-INS-CLEANUP", "InstallFailureCleanup"),
		["修复安装"] = new StepPresentation("InstallStepRepair", "ZC-INS-REPAIR", "InstallFailureGeneric"),
		["安装中断"] = new StepPresentation("WizardInstallInterrupted", "ZC-INS-INTERRUPTED", "InstallFailureGeneric"),
		["回滚"] = new StepPresentation("InstallStepRollback", "ZC-INS-ROLLBACK", "InstallFailureRollback"),
		["创建回滚点"] = new StepPresentation("InstallStepCreateRollback", "ZC-INS-ROLLBACK", "InstallFailureRollback"),
		["清理回滚点"] = new StepPresentation("InstallStepCleanupRollbackPoint", "ZC-INS-CLEANUP", "InstallFailureCleanup"),
		["卸载回滚"] = new StepPresentation("InstallStepUninstallRollback", "ZC-INS-ROLLBACK", "InstallFailureRollback"),
		["隔离计划任务"] = new StepPresentation("InstallStepIsolateTask", "ZC-INS-TASK", "InstallFailureTask"),
		["隔离运行数据"] = new StepPresentation("InstallStepIsolateData", "ZC-INS-DATA", "InstallFailureFiles"),
		["清理隔离数据"] = new StepPresentation("InstallStepCleanupIsolatedData", "ZC-INS-CLEANUP", "InstallFailureCleanup"),
		["删除计划任务"] = new StepPresentation("InstallStepDeleteTask", "ZC-INS-TASK", "InstallFailureTask"),
		["删除程序文件"] = new StepPresentation("InstallStepDeleteFiles", "ZC-INS-FILES", "InstallFailureFiles"),
		["删除运行数据"] = new StepPresentation("InstallStepDeleteData", "ZC-INS-DATA", "InstallFailureFiles"),
		["保留运行数据"] = new StepPresentation("InstallStepKeepData", "ZC-INS-DATA", "InstallFailureFiles"),
		["安排重启后清理"] = new StepPresentation("InstallStepDeferredCleanup", "ZC-INS-CLEANUP", "InstallFailureCleanup"),
		["安排退出后清理"] = new StepPresentation("InstallStepDeferredCleanup", "ZC-INS-CLEANUP", "InstallFailureCleanup")
	};

	public static InstallStepDisplay Present(InstallStep step)
	{
		StepPresentation value;
		bool flag = Presentations.TryGetValue(step.Title, out value);
		string title = (flag ? App.Localization.Text(value.TitleKey) : App.Localization.Text("InstallStepOther"));
		if (step.Success)
		{
			return new InstallStepDisplay(title, Success: true, App.Localization.Text("InstallStepCompleted"), step.Detail);
		}
		string title2 = step.Title;
		bool flag2 = ((title2 == "安装中断" || title2 == "修复安装") ? true : false);
		bool flag3 = flag2;
		string text = ((flag && !flag3) ? value.ErrorCode : ClassifyUnknownFailure(step.Detail));
		string key = ((flag && !flag3) ? value.FailureKey : FailureKeyForCode(text));
		string text2 = App.Localization.Text(key);
		string text3 = App.Localization.Format("InstallStepFailureSummary", text2, text);
		if (TryGetProtectedResidualPath(step.Title, step.Detail, out string path))
		{
			text3 = App.Localization.Format("InstallStepResidualPath", text3, path);
		}
		return new InstallStepDisplay(title, Success: false, text3, RedactTechnicalDetail(step.Detail), text);
	}

	public static string FormatFailureWithTechnicalDetail(InstallStep step)
	{
		InstallStepDisplay installStepDisplay = Present(step);
		if (string.IsNullOrWhiteSpace(installStepDisplay.TechnicalDetail))
		{
			return "· " + installStepDisplay.Title + ": " + installStepDisplay.Detail;
		}
		return $"· {installStepDisplay.Title}: {installStepDisplay.Detail}{Environment.NewLine}  {App.Localization.Text("InstallTechnicalDetail")}: {installStepDisplay.TechnicalDetail}";
	}

	private static string RedactTechnicalDetail(string detail)
	{
		string text = Regex.Replace(detail, "(?i)(api[_ -]?token|swarm[_ -]?key|authorization\\s*:\\s*bearer)\\s*[=:]\\s*[^\\s;，；]+", "$1=<redacted>", RegexOptions.CultureInvariant);
		if (text.Length > 4000)
		{
			return text.Substring(0, 4000) + "…";
		}
		return text;
	}

	private static string ClassifyUnknownFailure(string detail)
	{
		if (ContainsAny(detail, "swarm.key", "private-network key"))
		{
			return "ZC-INS-KEY";
		}
		if (ContainsAny(detail, "agent-config", "allowed_peers", "bootstrap", "rendezvous"))
		{
			return "ZC-INS-CONFIG";
		}
		if (ContainsAny(detail, "SHA-256", "Authenticode", "manifest", "载荷", "哈希"))
		{
			return "ZC-INS-PAYLOAD";
		}
		if (ContainsAny(detail, "Task Scheduler", "计划任务", "LogonTrigger"))
		{
			return "ZC-INS-TASK";
		}
		if (ContainsAny(detail, "ACL", "DACL", "权限", "access denied", "Unauthorized"))
		{
			return "ZC-INS-ACL";
		}
		if (ContainsAny(detail, "回滚", "rollback"))
		{
			return "ZC-INS-ROLLBACK";
		}
		if (ContainsAny(detail, "启动", "ready", "health", "API"))
		{
			return "ZC-INS-START";
		}
		if (ContainsAny(detail, "停止", "terminate", "kill"))
		{
			return "ZC-INS-STOP";
		}
		if (ContainsAny(detail, "文件", "目录", "path", "file", "directory"))
		{
			return "ZC-INS-FILES";
		}
		return "ZC-INS-UNKNOWN";
	}

	private static string FailureKeyForCode(string code)
	{
		return code switch
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
			_ => "InstallFailureGeneric", 
		};
	}

	private static bool ContainsAny(string value, params string[] candidates)
	{
		return candidates.Any((string candidate) => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
	}

	private static bool TryGetProtectedResidualPath(string title, string detail, out string path)
	{
		path = "";
		if ((!(title == "清理隔离数据") && !(title == "清理回滚点")) || 1 == 0)
		{
			return false;
		}
		string text = ((title == "清理隔离数据") ? "安全隔离数据仍保留：" : "受保护回滚点仍保留：");
		if (!detail.StartsWith(text, StringComparison.Ordinal))
		{
			return false;
		}
		int length = text.Length;
		string text2 = detail.Substring(length, detail.Length - length).Split('；', 2)[0].Trim();
		if (!Path.IsPathFullyQualified(text2))
		{
			return false;
		}
		path = text2;
		return true;
	}
}
