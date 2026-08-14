#nullable disable warnings
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace ZhanClawControl.Services;

public sealed class ScheduledTaskService
{
	private readonly record struct TaskLookup<T>(bool Found, T Value);

	private const string TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

	private const int TaskCreateOrUpdate = 6;

	private const int TaskDontAddPrincipalAce = 16;

	private const int TaskLogonInteractiveToken = 3;

	private const int TaskEnumHidden = 1;

	private const int OwnerSecurityInformation = 1;

	private const int DaclSecurityInformation = 4;

	private const int TaskFullControl = 2032127;

	private const int TaskReadAndExecute = 1179817;

	private const int HResultFileNotFound = -2147024894;

	private const int HResultPathNotFound = -2147024893;

	public Task<TaskState> GetStateAsync(CancellationToken ct = default(CancellationToken))
	{
		ct.ThrowIfCancellationRequested();
		try
		{
			TaskLookup<TaskState> taskLookup = LookupTask((dynamic task) => (int)task.State switch
			{
				1 => TaskState.Disabled, 
				3 => TaskState.Ready, 
				4 => TaskState.Running, 
				_ => TaskState.Unknown, 
			});
			return Task.FromResult(taskLookup.Found ? taskLookup.Value : TaskState.NotInstalled);
		}
		catch
		{
			return Task.FromResult(TaskState.Unknown);
		}
	}

	public Task<ScheduledTaskInspection> InspectAsync(CancellationToken ct = default(CancellationToken))
	{
		ct.ThrowIfCancellationRequested();
		ScheduledTaskSnapshot value;
		try
		{
			TaskLookup<ScheduledTaskSnapshot> taskLookup = LookupTask(new Func<object, ScheduledTaskSnapshot>(ReadTaskSnapshot));
			if (!taskLookup.Found)
			{
				return Task.FromResult(new ScheduledTaskInspection(Exists: false, MatchesExpectedDefinition: false, "", "", new string[1] { "计划任务不存在。" }));
			}
			value = taskLookup.Value;
		}
		catch (Exception exception)
		{
			return Task.FromResult(new ScheduledTaskInspection(Exists: true, MatchesExpectedDefinition: false, "", "", new string[1] { "计划任务查询失败，无法证明其不存在。" }, QueryFailed: true, DescribeException(exception)));
		}
		string xml = value.Xml;
		List<string> list = new List<string>();
		try
		{
			XElement xElement = XDocument.Parse(xml, LoadOptions.PreserveWhitespace).Root ?? throw new InvalidDataException("任务 XML 缺少根元素。");
			if (!string.Equals(xElement.Name.LocalName, "Task", StringComparison.Ordinal) || !string.Equals(xElement.Name.NamespaceName, "http://schemas.microsoft.com/windows/2004/02/mit/task", StringComparison.Ordinal))
			{
				throw new InvalidDataException("任务 XML 根元素或命名空间无效。");
			}
			XNamespace xNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
			List<XElement> list2 = xElement.Element(xNamespace + "Principals")?.Elements().ToList() ?? new List<XElement>();
			XElement obj = ((list2.Count == 1 && list2[0].Name == xNamespace + "Principal") ? list2[0] : null);
			string text = Value(obj, xNamespace, "UserId");
			if (obj == null)
			{
				list.Add("Principal 数量或类型不是精确的 1 个。");
			}
			if (text.Length == 0)
			{
				list.Add("任务缺少 UserId。");
			}
			Expect(Value(obj, xNamespace, "LogonType"), "InteractiveToken", "LogonType", list);
			string text2 = Value(obj, xNamespace, "RunLevel");
			if (text2.Length > 0)
			{
				Expect(text2, "LeastPrivilege", "RunLevel", list);
			}
			if (value.RunLevel != 0)
			{
				list.Add($"有效 RunLevel 不是 LeastPrivilege/LUA（actual={value.RunLevel}）。");
			}
			RejectChildren(obj, xNamespace, (IReadOnlyCollection<string>)(object)new string[4] { "UserId", "LogonType", "DisplayName", "RunLevel" }, "Principal", list);
			ValidateTaskSecurityDescriptor(value.SecurityDescriptor, text, "任务安全描述符", requireControlledOwner: true, list);
			List<XElement> list3 = xElement.Element(xNamespace + "Triggers")?.Elements().ToList() ?? new List<XElement>();
			XElement xElement2 = ((list3.Count == 1 && list3[0].Name == xNamespace + "LogonTrigger") ? list3[0] : null);
			if (xElement2 == null)
			{
				list.Add("Triggers 必须只包含 1 个 LogonTrigger。");
			}
			if (!SameAccount(text, Value(xElement2, xNamespace, "UserId")))
			{
				list.Add("Trigger UserId 与 Principal UserId 不一致。");
			}
			ExpectBooleanDefault(Value(xElement2, xNamespace, "Enabled"), expected: true, "LogonTrigger.Enabled", list);
			RejectChildren(xElement2, xNamespace, (IReadOnlyCollection<string>)(object)new string[2] { "Enabled", "UserId" }, "LogonTrigger", list);
			XElement xElement3 = xElement.Element(xNamespace + "Actions");
			List<XElement> list4 = xElement3?.Elements().ToList() ?? new List<XElement>();
			XElement obj2 = ((list4.Count == 1 && list4[0].Name == xNamespace + "Exec") ? list4[0] : null);
			if (obj2 == null)
			{
				list.Add("Actions 必须只包含 1 个 Exec。");
			}
			if (!string.Equals(xElement3?.Attribute("Context")?.Value, "Author", StringComparison.Ordinal))
			{
				list.Add("Actions Context 不是 Author。");
			}
			if (!PathsEqual(Value(obj2, xNamespace, "Command"), AppPaths.ControlExe))
			{
				list.Add("Command 未精确指向安装目录后台宿主。");
			}
			if (!string.Equals(Value(obj2, xNamespace, "Arguments"), "--run-agent", StringComparison.Ordinal))
			{
				list.Add("Arguments 不是精确的 --run-agent。");
			}
			if (!PathsEqual(Value(obj2, xNamespace, "WorkingDirectory"), "C:\\Program Files\\P2PAgent"))
			{
				list.Add("WorkingDirectory 不是安装目录。");
			}
			XElement? xElement4 = xElement.Element(xNamespace + "Settings");
			if (xElement4 == null)
			{
				list.Add("任务缺少 Settings。");
			}
			bool? flag = ReadBooleanDefault(Value(xElement4, xNamespace, "Enabled"), true, "Settings.Enabled", list);
			if (flag.HasValue)
			{
				bool valueOrDefault = flag == true;
				if (value.Enabled != valueOrDefault)
				{
					list.Add("RegisteredTask.Enabled 与任务 XML 有效 Enabled 不一致。");
				}
			}
			ExpectBooleanDefault(Value(xElement4, xNamespace, "AllowStartOnDemand"), expected: true, "AllowStartOnDemand", list);
			ExpectDefault(Value(xElement4, xNamespace, "MultipleInstancesPolicy"), "IgnoreNew", "MultipleInstancesPolicy", list);
			ExpectBoolean(Value(xElement4, xNamespace, "DisallowStartIfOnBatteries"), expected: false, "DisallowStartIfOnBatteries", list);
			ExpectBoolean(Value(xElement4, xNamespace, "StopIfGoingOnBatteries"), expected: false, "StopIfGoingOnBatteries", list);
			ExpectBooleanDefault(Value(xElement4, xNamespace, "AllowHardTerminate"), expected: true, "AllowHardTerminate", list);
			ExpectBoolean(Value(xElement4, xNamespace, "StartWhenAvailable"), expected: true, "StartWhenAvailable", list);
			ExpectBooleanDefault(Value(xElement4, xNamespace, "RunOnlyIfNetworkAvailable"), expected: false, "RunOnlyIfNetworkAvailable", list);
			ExpectBooleanDefault(Value(xElement4, xNamespace, "WakeToRun"), expected: false, "WakeToRun", list);
			Expect(Value(xElement4, xNamespace, "ExecutionTimeLimit"), "PT0S", "ExecutionTimeLimit", list);
			ExpectDefault(Value(xElement4, xNamespace, "Priority"), "7", "Priority", list);
			ExpectBooleanDefault(Value(xElement4, xNamespace, "Hidden"), expected: false, "Hidden", list);
			ExpectBooleanDefault(Value(xElement4, xNamespace, "RunOnlyIfIdle"), expected: false, "RunOnlyIfIdle", list);
			ExpectBooleanDefault(Value(xElement4, xNamespace, "DisallowStartOnRemoteAppSession"), expected: false, "DisallowStartOnRemoteAppSession", list);
			ExpectBoolean(Value(xElement4, xNamespace, "UseUnifiedSchedulingEngine"), expected: true, "UseUnifiedSchedulingEngine", list);
			XElement? parent = xElement4?.Element(xNamespace + "IdleSettings");
			ExpectBoolean(Value(parent, xNamespace, "StopOnIdleEnd"), expected: false, "IdleSettings.StopOnIdleEnd", list);
			ExpectBoolean(Value(parent, xNamespace, "RestartOnIdle"), expected: false, "IdleSettings.RestartOnIdle", list);
			XElement? parent2 = xElement4?.Element(xNamespace + "RestartOnFailure");
			Expect(Value(parent2, xNamespace, "Interval"), "PT1M", "RestartOnFailure.Interval", list);
			Expect(Value(parent2, xNamespace, "Count"), "3", "RestartOnFailure.Count", list);
			RejectChildren(xElement4, xNamespace, (IReadOnlyCollection<string>)(object)new string[17]
			{
				"AllowStartOnDemand", "RestartOnFailure", "MultipleInstancesPolicy", "DisallowStartIfOnBatteries", "StopIfGoingOnBatteries", "AllowHardTerminate", "StartWhenAvailable", "RunOnlyIfNetworkAvailable", "WakeToRun", "ExecutionTimeLimit",
				"Priority", "IdleSettings", "Enabled", "Hidden", "RunOnlyIfIdle", "DisallowStartOnRemoteAppSession", "UseUnifiedSchedulingEngine"
			}, "Settings", list);
			string text3 = Value(xElement.Element(xNamespace + "RegistrationInfo"), xNamespace, "SecurityDescriptor");
			if (text3.Length > 0)
			{
				ValidateTaskSecurityDescriptor(text3, text, "RegistrationInfo.SecurityDescriptor", requireControlledOwner: false, list);
			}
			if (xElement.Element(xNamespace + "Data") != null)
			{
				list.Add("任务不允许附加 Data。");
			}
			RejectChildren(xElement, xNamespace, (IReadOnlyCollection<string>)(object)new string[5] { "RegistrationInfo", "Triggers", "Principals", "Settings", "Actions" }, "Task", list);
			return Task.FromResult(new ScheduledTaskInspection(Exists: true, list.Count == 0, text, xml, list, QueryFailed: false, "", value.Enabled, value.RunLevel, value.SecurityDescriptor));
		}
		catch (Exception ex)
		{
			list.Add("任务 XML 无法解析：" + ex.Message);
			return Task.FromResult(new ScheduledTaskInspection(Exists: true, MatchesExpectedDefinition: false, "", xml, list));
		}
	}

	public Task<ProcessResult> RegisterAsync(string runAsUser, CancellationToken ct = default(CancellationToken))
	{
		return RegisterAsync(runAsUser, enabled: true, ct);
	}

	public async Task<ProcessResult> RegisterAsync(string runAsUser, bool enabled, CancellationToken ct = default(CancellationToken))
	{
		string value = RuntimeSecurityService.ResolveInteractiveUserSid(runAsUser).Value;
		ProcessResult processResult = await RegisterXmlAsync(BuildTaskXml(value, enabled), ct).ConfigureAwait(continueOnCapturedContext: false);
		if (!processResult.Success)
		{
			return processResult;
		}
		ScheduledTaskInspection scheduledTaskInspection = await InspectAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		if (scheduledTaskInspection.QueryFailed)
		{
			return Failure("任务已提交，但注册后查询失败：" + scheduledTaskInspection.QueryError);
		}
		return scheduledTaskInspection.MatchesExpectedDefinition ? Success("Task Scheduler COM registration and exact-definition verification completed.") : Failure("任务已创建，但精确定义校验失败：" + string.Join("；", scheduledTaskInspection.Issues));
	}

	public Task<ProcessResult> RegisterXmlAsync(string xml, CancellationToken ct = default(CancellationToken))
	{
		ct.ThrowIfCancellationRequested();
		try
		{
			string account = ReadPrincipalUserId(xml);
			string runAsSid = RuntimeSecurityService.ResolveInteractiveUserSid(account).Value;
			string taskSecurityDescriptor = BuildTaskSecurityDescriptor(runAsSid);
			WithFolder(delegate(dynamic folder)
			{
				object value = null;
				try
				{
					value = folder.RegisterTask("P2P Agent", xml, 22, runAsSid, null, 3, taskSecurityDescriptor);
				}
				finally
				{
					ReleaseCom(value);
				}
				return 0;
			});
			return Task.FromResult(Success("Task Scheduler COM registration completed."));
		}
		catch (Exception exception)
		{
			return Task.FromResult(Failure(DescribeException(exception)));
		}
	}

	public Task<ProcessResult> StartAsync(CancellationToken ct = default(CancellationToken))
	{
		return StartAsync(allowTaskMaintenance: false, ct);
	}

	public async Task<ProcessResult> StartAsync(bool allowTaskMaintenance, CancellationToken ct = default(CancellationToken), bool allowTrustedRollbackPayload = false)
	{
		try
		{
			RuntimeSecurityService.ValidateRuntimeProvisioningStartBoundary();
			if (!allowTaskMaintenance && RuntimeSecurityService.HasMaintenanceArtifacts)
			{
				throw new InvalidOperationException("检测到未完成的安装/修复/卸载维护事务；普通启动已失败关闭，请先完成恢复。");
			}
			if (allowTaskMaintenance && !RuntimeSecurityService.HasMaintenanceArtifacts)
			{
				throw new InvalidOperationException("受控维护启动缺少活动维护事务。 ");
			}
		}
		catch (Exception ex)
		{
			return Failure("拒绝启动：" + ex.Message);
		}
		ScheduledTaskInspection inspection = await InspectAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		if (inspection.QueryFailed || !inspection.Exists || !inspection.MatchesExpectedDefinition)
		{
			return Failure("拒绝启动未通过精确定义校验的同名任务：" + string.Join("；", from x in inspection.Issues.Append(inspection.QueryError)
				where !string.IsNullOrWhiteSpace(x)
				select x));
		}
		if (allowTaskMaintenance && inspection.EffectiveEnabled)
		{
			return Failure("拒绝创建维护启动许可：精确任务在受控启动前必须保持 disabled。 ");
		}
		bool maintenancePrepared = false;
		if (allowTaskMaintenance)
		{
			try
			{
				RuntimeSecurityService.DeleteMaintenanceStartPermitIfPresent(inspection.RunAsUser);
				if (!allowTrustedRollbackPayload)
				{
					await RuntimeSecurityService.ValidateAgentPayloadAsync(AppPaths.AgentExe, ct).ConfigureAwait(continueOnCapturedContext: false);
				}
				else
				{
					RuntimeSecurityService.ValidateTrustedAgentPublisherForRollback(AppPaths.AgentExe);
				}
				RuntimeSecurityService.RejectReparsePoint(AppPaths.AgentExe);
				using FileStream stream = new FileStream(AppPaths.AgentExe, FileMode.Open, FileAccess.Read, FileShare.Read);
				string agentSha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(continueOnCapturedContext: false));
				maintenancePrepared = true;
				RuntimeSecurityService.CreateMaintenanceStartPermit(inspection.RunAsUser, agentSha, allowTrustedRollbackPayload);
				// Publish the recoverable phase before relaxing execute ACLs or
				// temporarily enabling the task. A power loss from this point can be
				// recognized and normalized by Repair before any new mutation.
				RuntimeSecurityService.MarkTaskMaintenanceValidationReady();
				RuntimeSecurityService.RestoreAgentExecutionForControlledStart(AppPaths.AgentExe);
			}
			catch (Exception ex2)
			{
				return await FailMaintenanceStartAsync(inspection.RunAsUser,
					"维护启动准备失败：" + ex2.Message).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		try
		{
			ct.ThrowIfCancellationRequested();
			bool initiallyEnabled = inspection.EffectiveEnabled;
			WithTask(delegate(dynamic task)
			{
				object value = null;
				try
				{
					if (!initiallyEnabled)
					{
						task.Enabled = true;
					}
					value = task.Run(null);
				}
				finally
				{
					ReleaseCom(value);
					if (!initiallyEnabled)
					{
						task.Enabled = false;
					}
				}
				return 0;
			});
		}
		catch (Exception exception)
		{
			if (maintenancePrepared)
			{
				return await FailMaintenanceStartAsync(inspection.RunAsUser, DescribeException(exception)).ConfigureAwait(continueOnCapturedContext: false);
			}
			return Failure(DescribeException(exception));
		}
		DateTime deadline = DateTime.UtcNow.AddSeconds(10.0);
		try
		{
			while (DateTime.UtcNow < deadline)
			{
				ct.ThrowIfCancellationRequested();
				if (IsAgentProcessRunning())
				{
					if (maintenancePrepared)
					{
						try
						{
							RuntimeSecurityService.RestrictAgentExecutionForMaintenance(AppPaths.AgentExe);
						}
						catch (Exception ex4)
						{
							return await FailMaintenanceStartAsync(inspection.RunAsUser, "Agent 已出现但维护执行 ACL 无法重新限制：" + ex4.Message).ConfigureAwait(continueOnCapturedContext: false);
						}
					}
					return Success("Task started and exact Agent process observed.");
				}
				await Task.Delay(200, ct).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		catch (OperationCanceledException)
		{
			if (maintenancePrepared)
			{
				await FailMaintenanceStartAsync(inspection.RunAsUser, "受控启动已取消。 ").ConfigureAwait(continueOnCapturedContext: false);
			}
			throw;
		}
		if (maintenancePrepared)
		{
			return await FailMaintenanceStartAsync(inspection.RunAsUser, "任务运行命令已接受，但 10 秒内未出现本产品宿主或 Agent 进程。 ").ConfigureAwait(continueOnCapturedContext: false);
		}
		return Failure("任务运行命令已接受，但 10 秒内未出现本产品宿主或 Agent 进程。");
	}

	private async Task<ProcessResult> FailMaintenanceStartAsync(string runAsUser, string detail)
	{
		List<string> errors = new List<string> { detail };
		using CancellationTokenSource cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(15.0));
		bool restricted = false;
		bool stoppedAfterRestriction = false;
		try
		{
			ProcessResult processResult = await StopAsync(cleanupCts.Token).ConfigureAwait(continueOnCapturedContext: false);
			if (!processResult.Success)
			{
				errors.Add("失败启动停机复核失败：" + processResult.CombinedOutput);
			}
		}
		catch (Exception ex)
		{
			errors.Add("失败启动停机复核失败：" + ex.Message);
		}
		try
		{
			RuntimeSecurityService.DeleteMaintenanceStartPermitIfPresent(runAsUser);
		}
		catch (Exception ex2)
		{
			errors.Add("一次性启动许可清理失败：" + ex2.Message);
		}
		try
		{
			RuntimeSecurityService.RestrictAgentExecutionForMaintenance(AppPaths.AgentExe);
			restricted = true;
		}
		catch (Exception ex3)
		{
			errors.Add("Agent 执行 ACL 重新限制失败：" + ex3.Message);
		}
		if (restricted)
		{
			try
			{
				// Close the Stop -> ACL restriction race before publishing Mutation.
				ProcessResult processResult2 = await StopAsync(cleanupCts.Token).ConfigureAwait(continueOnCapturedContext: false);
				if (processResult2.Success)
				{
					stoppedAfterRestriction = true;
				}
				else
				{
					errors.Add("执行 ACL 限制后的停机复核失败：" + processResult2.CombinedOutput);
				}
			}
			catch (Exception ex4)
			{
				errors.Add("执行 ACL 限制后的停机复核失败：" + ex4.Message);
			}
		}
		if (restricted && stoppedAfterRestriction)
		{
			try
			{
				RuntimeSecurityService.RestoreTaskMaintenanceMutationPhaseIfPresent();
			}
			catch (Exception ex5)
			{
				errors.Add("计划任务维护阶段无法恢复为 Mutation：" + ex5.Message);
			}
		}
		return Failure(string.Join("；", errors));
	}

	public async Task<ProcessResult> StopAsync(CancellationToken ct = default(CancellationToken))
	{
		List<string> errors = new List<string>();
		try
		{
			ct.ThrowIfCancellationRequested();
			WithTask(delegate(dynamic task)
			{
				task.Stop(0);
				return 0;
			});
		}
		catch (Exception exception) when (IsTaskNotFound(exception))
		{
		}
		catch (Exception exception2)
		{
			errors.Add("停止计划任务失败：" + DescribeException(exception2));
		}
		ProcessResult processResult = KillAgentProcesses();
		if (!processResult.Success)
		{
			errors.Add(processResult.CombinedOutput);
		}
		DateTime deadline = DateTime.UtcNow.AddSeconds(5.0);
		while (DateTime.UtcNow < deadline && AnyProductProcessRunning())
		{
			ct.ThrowIfCancellationRequested();
			await Task.Delay(150, ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		if (AnyProductProcessRunning())
		{
			errors.Add("本产品 Agent/宿主进程仍在运行。");
		}
		return (errors.Count == 0) ? Success("Task and product processes stopped.") : Failure(string.Join(Environment.NewLine, errors));
	}

	public Task<ProcessResult> DeleteAsync(CancellationToken ct = default(CancellationToken))
	{
		ct.ThrowIfCancellationRequested();
		try
		{
			WithFolder(delegate(dynamic folder)
			{
				folder.DeleteTask("P2P Agent", 0);
				return 0;
			});
			return Task.FromResult(Success("Task deleted."));
		}
		catch (Exception exception) when (IsTaskNotFound(exception))
		{
			return Task.FromResult(Success("Task was already absent."));
		}
		catch (Exception exception2)
		{
			return Task.FromResult(Failure(DescribeException(exception2)));
		}
	}

	public Task<ProcessResult> SetEnabledAsync(bool enabled, CancellationToken ct = default(CancellationToken))
	{
		return SetEnabledAsync(enabled, allowTaskMaintenance: false, ct);
	}

	public async Task<ProcessResult> SetEnabledAsync(bool enabled, bool allowTaskMaintenance, CancellationToken ct = default(CancellationToken))
	{
		ct.ThrowIfCancellationRequested();
		try
		{
			if (!allowTaskMaintenance && RuntimeSecurityService.TryReadTaskMaintenanceMarker(out var _))
			{
				return Failure("检测到未完成的安装/修复维护事务；拒绝修改登录自启偏好，请先完成修复。");
			}
		}
		catch (Exception ex)
		{
			return Failure("计划任务维护意图无法安全核验：" + ex.Message);
		}
		ScheduledTaskInspection scheduledTaskInspection = await InspectAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		if (scheduledTaskInspection.QueryFailed || !scheduledTaskInspection.Exists || !scheduledTaskInspection.MatchesExpectedDefinition)
		{
			return Failure("拒绝修改未通过精确定义校验的同名任务：" + string.Join("；", from value in scheduledTaskInspection.Issues.Append(scheduledTaskInspection.QueryError)
				where !string.IsNullOrWhiteSpace(value)
				select value));
		}
		try
		{
			WithTask(delegate(dynamic task)
			{
				task.Enabled = enabled;
				return 0;
			});
			return Success("Task state updated.");
		}
		catch (Exception exception)
		{
			return Failure(DescribeException(exception));
		}
	}

	public static bool IsAgentProcessRunning()
	{
		return IsExactProcessRunning("p2p-agent", AppPaths.AgentExe);
	}

	public static ProcessResult KillAgentProcesses()
	{
		List<string> list = new List<string>();
		KillExactProcesses("ZhanClawControl", AppPaths.ControlExe, list);
		KillExactProcesses("p2p-agent", AppPaths.AgentExe, list);
		if (list.Count != 0)
		{
			return Failure(string.Join(Environment.NewLine, list));
		}
		return Success("");
	}

	private static T WithTask<T>(Func<dynamic, T> action)
	{
		TaskLookup<T> taskLookup = LookupTask(action);
		if (!taskLookup.Found)
		{
			throw new FileNotFoundException("计划任务不存在：P2P Agent");
		}
		return taskLookup.Value;
	}

	private static TaskLookup<T> LookupTask<T>(Func<dynamic, T> action)
	{
		try
		{
			return WithFolder(delegate(dynamic folder)
			{
				dynamic val = null;
				try
				{
					val = folder.GetTask("P2P Agent");
					return new TaskLookup<T>(true, ((Func<object, T>)action)(val));
				}
				finally
				{
					ReleaseCom((object?)val);
				}
			});
		}
		catch (Exception exception) when (IsTaskNotFound(exception))
		{
			return WithFolder((dynamic folder) => ScheduledTaskService.EnumerateTask(folder, (Func<object, T>)action));
		}
	}

	private static TaskLookup<T> EnumerateTask<T>(dynamic folder, Func<dynamic, T> action)
	{
		dynamic val = null;
		try
		{
			val = folder.GetTasks(1);
			int num = (int)val.Count;
			for (int i = 1; i <= num; i++)
			{
				dynamic val2 = null;
				try
				{
					val2 = val.Item(i);
					if (string.Equals((string)val2.Name, "P2P Agent", StringComparison.OrdinalIgnoreCase))
					{
						return new TaskLookup<T>(true, ((Func<object, T>)action)(val2));
					}
				}
				finally
				{
					ReleaseCom((object?)val2);
				}
			}
			return new TaskLookup<T>(Found: false, default(T));
		}
		finally
		{
			ReleaseCom((object?)val);
		}
	}

	private static T WithFolder<T>(Func<dynamic, T> action)
	{
		dynamic val = null;
		dynamic val2 = null;
		try
		{
			val = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true) ?? throw new PlatformNotSupportedException("Task Scheduler COM unavailable.")) ?? throw new COMException("Cannot create Task Scheduler COM service.");
			val.Connect();
			val2 = val.GetFolder("\\");
			return ((Func<object, T>)action)(val2);
		}
		finally
		{
			ReleaseCom((object?)val2);
			ReleaseCom((object?)val);
		}
	}

	private static void ReleaseCom(object? value)
	{
		if (value != null && Marshal.IsComObject(value))
		{
			try
			{
				Marshal.FinalReleaseComObject(value);
			}
			catch
			{
			}
		}
	}

	private static bool IsTaskNotFound(Exception exception)
	{
		for (Exception ex = exception; ex != null; ex = Unwrap(ex))
		{
			int hResult = ex.HResult;
			if ((uint)(hResult - -2147024894) <= 1u)
			{
				return true;
			}
		}
		return false;
		static Exception? Unwrap(Exception current)
		{
			if (current is TargetInvocationException)
			{
				Exception innerException = current.InnerException;
				if (innerException != null)
				{
					return innerException;
				}
			}
			else if (current is AggregateException ex2 && ex2.InnerExceptions.Count == 1)
			{
				return ex2.InnerExceptions[0];
			}
			return current.InnerException;
		}
	}

	private static string DescribeException(Exception exception)
	{
		Exception ex = exception;
		while (true)
		{
			Exception ex2 = Unwrap(ex);
			if (ex2 == null)
			{
				break;
			}
			ex = ex2;
		}
		return $"{ex.Message} (HRESULT 0x{ex.HResult:X8})";
		static Exception? Unwrap(Exception current)
		{
			if (current is TargetInvocationException)
			{
				Exception innerException = current.InnerException;
				if (innerException != null)
				{
					return innerException;
				}
			}
			else if (current is AggregateException ex3 && ex3.InnerExceptions.Count == 1)
			{
				return ex3.InnerExceptions[0];
			}
			return current.InnerException;
		}
	}

	private static string ReadPrincipalUserId(string xml)
	{
		XElement xElement = XDocument.Parse(xml).Root ?? throw new InvalidDataException("任务 XML 缺少根元素。");
		XNamespace ns = xElement.Name.Namespace;
		List<string> obj = (from p in xElement.Element(ns + "Principals")?.Elements(ns + "Principal")
			select Value(p, ns, "UserId") into x
			where x.Length > 0
			select x).ToList() ?? new List<string>();
		if (obj.Count != 1)
		{
			throw new InvalidDataException("任务 XML 必须含唯一 Principal/UserId。");
		}
		return obj[0];
	}

	public static bool ReadTaskEnabled(string xml)
	{
		XElement? obj = XDocument.Parse(xml).Root ?? throw new InvalidDataException("任务 XML 缺少根元素。");
		XNamespace xNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
		string text = Value(obj.Element(xNamespace + "Settings"), xNamespace, "Enabled");
		if (text.Length == 0)
		{
			return true;
		}
		try
		{
			return XmlConvert.ToBoolean(text.ToLowerInvariant());
		}
		catch (FormatException innerException)
		{
			throw new InvalidDataException("Settings.Enabled 不是合法布尔值。", innerException);
		}
	}

	public async Task<bool> ReadEffectiveEnabledAsync(CancellationToken ct = default(CancellationToken))
	{
		ScheduledTaskInspection scheduledTaskInspection = await InspectAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		if (scheduledTaskInspection.QueryFailed || !scheduledTaskInspection.Exists)
		{
			throw new InvalidOperationException("无法读取计划任务有效 Enabled：" + scheduledTaskInspection.QueryError);
		}
		return scheduledTaskInspection.EffectiveEnabled;
	}

	private static ScheduledTaskSnapshot ReadTaskSnapshot(dynamic task)
	{
		dynamic val = null;
		dynamic val2 = null;
		try
		{
			val = task.Definition;
			val2 = val.Principal;
			return new ScheduledTaskSnapshot((string)task.Xml, Convert.ToBoolean(task.Enabled, CultureInfo.InvariantCulture), Convert.ToInt32(val2.RunLevel, CultureInfo.InvariantCulture), (string)task.GetSecurityDescriptor(5));
		}
		finally
		{
			ReleaseCom((object?)val2);
			ReleaseCom((object?)val);
		}
	}

	private static string Value(XElement? parent, XNamespace ns, string name)
	{
		return parent?.Element(ns + name)?.Value.Trim() ?? "";
	}

	private static void Expect(string actual, string expected, string field, ICollection<string> issues)
	{
		if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
		{
			issues.Add(field + " 不是 " + expected + "。");
		}
	}

	private static void ExpectDefault(string actual, string expected, string field, ICollection<string> issues)
	{
		if (actual.Length > 0 && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
		{
			issues.Add(field + " 不是 " + expected + "。");
		}
	}

	private static void ExpectBoolean(string actual, bool expected, string field, ICollection<string> issues)
	{
		bool? flag = ReadBooleanDefault(actual, null, field, issues);
		if (flag.HasValue)
		{
			bool valueOrDefault = flag == true;
			if (valueOrDefault != expected)
			{
				issues.Add(field + " 不是 " + expected.ToString().ToLowerInvariant() + "。");
			}
		}
	}

	private static void ExpectBooleanDefault(string actual, bool expected, string field, ICollection<string> issues)
	{
		bool? flag = ReadBooleanDefault(actual, expected, field, issues);
		if (flag.HasValue)
		{
			bool valueOrDefault = flag == true;
			if (valueOrDefault != expected)
			{
				issues.Add(field + " 不是 " + expected.ToString().ToLowerInvariant() + "。");
			}
		}
	}

	private static bool? ReadBooleanDefault(string actual, bool? defaultValue, string field, ICollection<string> issues)
	{
		if (actual.Length == 0)
		{
			if (defaultValue.HasValue)
			{
				return defaultValue.Value;
			}
			issues.Add(field + " 缺失。");
			return null;
		}
		try
		{
			return XmlConvert.ToBoolean(actual.ToLowerInvariant());
		}
		catch (FormatException)
		{
			issues.Add(field + " 不是合法 xs:boolean。");
			return null;
		}
	}

	private static void RejectChildren(XElement? parent, XNamespace ns, IReadOnlyCollection<string> allowed, string field, ICollection<string> issues)
	{
		if (parent == null)
		{
			return;
		}
		foreach (XElement item in parent.Elements())
		{
			if (item.Name.Namespace != ns || !allowed.Contains<string>(item.Name.LocalName, StringComparer.Ordinal))
			{
				issues.Add(field + " 包含未获准节点：" + item.Name.LocalName + "。");
			}
		}
	}

	private static string BuildTaskSecurityDescriptor(string runAsSid)
	{
		return $"O:BAD:P(A;;0x{2032127:X};;;SY)(A;;0x{2032127:X};;;BA)(A;;0x{1179817:X};;;{runAsSid})";
	}

	private static void ValidateTaskSecurityDescriptor(string sddl, string runAsUser, string field, bool requireControlledOwner, ICollection<string> issues)
	{
		if (string.IsNullOrWhiteSpace(sddl))
		{
			issues.Add(field + " 缺失，无法证明任务写权限边界。");
			return;
		}
		RawSecurityDescriptor rawSecurityDescriptor;
		SecurityIdentifier securityIdentifier;
		try
		{
			rawSecurityDescriptor = new RawSecurityDescriptor(sddl);
			securityIdentifier = RuntimeSecurityService.ResolveInteractiveUserSid(runAsUser);
		}
		catch (Exception ex) when (((ex is ArgumentException || ex is InvalidDataException) ? 1 : 0) != 0)
		{
			issues.Add(field + " 无法解析：" + ex.Message);
			return;
		}
		if ((rawSecurityDescriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || rawSecurityDescriptor.DiscretionaryAcl == null)
		{
			issues.Add(field + " 没有有效 DACL。");
			return;
		}
		if ((rawSecurityDescriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0)
		{
			issues.Add(field + " 未禁止继承。");
		}
		SecurityIdentifier securityIdentifier2 = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
		SecurityIdentifier securityIdentifier3 = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
		bool flag = (object)rawSecurityDescriptor.Owner != null && (rawSecurityDescriptor.Owner.Equals(securityIdentifier3) || rawSecurityDescriptor.Owner.Equals(securityIdentifier2));
		if (requireControlledOwner && !flag)
		{
			issues.Add($"{field} owner 不是 Builtin Administrators 或 SYSTEM（actual={rawSecurityDescriptor.Owner?.Value ?? "<missing>"}）。");
		}
		else
		{
			SecurityIdentifier owner = rawSecurityDescriptor.Owner;
			if ((object)owner != null && !owner.Equals(securityIdentifier3) && !owner.Equals(securityIdentifier2))
			{
				issues.Add(field + " 声明了非预期 owner：" + owner.Value + "。");
			}
		}
		Dictionary<string, int> obj = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
		{
			[securityIdentifier2.Value] = 2032127,
			[securityIdentifier3.Value] = 2032127
		};
		string key = securityIdentifier.Value;
		obj[key] = 1179817;
		Dictionary<string, int> expected = obj;
		Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		AceEnumerator enumerator = rawSecurityDescriptor.DiscretionaryAcl.GetEnumerator();
		while (enumerator.MoveNext())
		{
			GenericAce current = enumerator.Current;
			if (current is CommonAce { AceQualifier: AceQualifier.AccessAllowed } commonAce && current.AceFlags == AceFlags.None && !commonAce.IsCallback)
			{
				SecurityIdentifier securityIdentifier4 = commonAce.SecurityIdentifier;
				if ((object)securityIdentifier4 != null)
				{
					if (!dictionary.TryAdd(securityIdentifier4.Value, commonAce.AccessMask))
					{
						issues.Add(field + " 对 SID " + securityIdentifier4.Value + " 含重复 ACE。");
					}
					continue;
				}
			}
			issues.Add($"{field} 包含非预期 ACE（type={current.AceType}, flags={current.AceFlags}）。");
		}
		foreach (KeyValuePair<string, int> item in expected)
		{
			item.Deconstruct(out key, out var value3);
			string text = key;
			int num = value3;
			if (!dictionary.TryGetValue(text, out var value4))
			{
				issues.Add(field + " 缺少受控主体 SID " + text + "。");
			}
			else if (value4 != num)
			{
				issues.Add($"{field} 的 SID {text} 权限不是预期值 0x{num:X}（actual=0x{value4:X}）。");
			}
		}
		foreach (string item2 in dictionary.Keys.Where((string sid) => !expected.ContainsKey(sid)))
		{
			issues.Add(field + " 包含额外主体 SID " + item2 + "。");
		}
	}

	private static bool AnyProductProcessRunning()
	{
		if (!IsExactProcessRunning("ZhanClawControl", AppPaths.ControlExe))
		{
			return IsAgentProcessRunning();
		}
		return true;
	}

	private static void KillExactProcesses(string name, string expectedPath, ICollection<string> errors)
	{
		Process[] processesByName = Process.GetProcessesByName(name);
		foreach (Process process in processesByName)
		{
			try
			{
				if (process.Id != Environment.ProcessId && IsProcessAtPath(process, expectedPath))
				{
					process.Kill(entireProcessTree: true);
					if (!process.WaitForExit(5000))
					{
						errors.Add($"进程未在超时内退出：{expectedPath} pid={process.Id}");
					}
				}
			}
			catch (Exception ex)
			{
				errors.Add($"结束进程失败：{expectedPath} pid={process.Id}：{ex.Message}");
			}
			finally
			{
				process.Dispose();
			}
		}
	}

	private static bool IsExactProcessRunning(string name, string expectedPath)
	{
		Process[] processesByName = Process.GetProcessesByName(name);
		try
		{
			Process[] array = processesByName;
			foreach (Process process in array)
			{
				try
				{
					if (process.Id != Environment.ProcessId && IsProcessAtPath(process, expectedPath))
					{
						return true;
					}
				}
				catch
				{
				}
			}
			return false;
		}
		finally
		{
			Process[] array = processesByName;
			for (int i = 0; i < array.Length; i++)
			{
				array[i].Dispose();
			}
		}
	}

	private static bool IsProcessAtPath(Process process, string path)
	{
		string text = process.MainModule?.FileName;
		if (text != null)
		{
			return PathsEqual(text, path);
		}
		return false;
	}

	private static bool PathsEqual(string left, string right)
	{
		try
		{
			return string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	private static bool SameAccount(string left, string right)
	{
		if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		try
		{
			return RuntimeSecurityService.ResolveAccountSid(left).Equals(RuntimeSecurityService.ResolveAccountSid(right));
		}
		catch
		{
			return false;
		}
	}

	private static ProcessResult Success(string output)
	{
		return new ProcessResult(0, output, "");
	}

	private static ProcessResult Failure(string error)
	{
		return new ProcessResult(-1, "", error);
	}

	private static string BuildTaskXml(string runAsSid, bool enabled)
	{
		string value = SecurityElement.Escape(runAsSid) ?? runAsSid;
		string value2 = SecurityElement.Escape(AppPaths.ControlExe) ?? AppPaths.ControlExe;
		string value3 = SecurityElement.Escape("--run-agent") ?? "--run-agent";
		string value4 = SecurityElement.Escape("C:\\Program Files\\P2PAgent") ?? "C:\\Program Files\\P2PAgent";
		string value5 = (enabled ? "true" : "false");
		return $"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n  <RegistrationInfo><Date>{DateTime.UtcNow.ToString("s", CultureInfo.InvariantCulture)}Z</Date><Author>ZhanClawControl</Author><Description>{"战 Claw 被控端"} - 后台进程</Description><URI>\\{"P2P Agent"}</URI></RegistrationInfo>\n  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{value}</UserId></LogonTrigger></Triggers>\n  <Principals><Principal id=\"Author\"><UserId>{value}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\n  <Settings><AllowStartOnDemand>true</AllowStartOnDemand><RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable><RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><WakeToRun>false</WakeToRun><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority><IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings><Enabled>{value5}</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession><UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine></Settings>\n  <Actions Context=\"Author\"><Exec><Command>{value2}</Command><Arguments>{value3}</Arguments><WorkingDirectory>{value4}</WorkingDirectory></Exec></Actions>\n</Task>";
	}
}
