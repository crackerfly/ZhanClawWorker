using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace ZhanClawControl.Services;

public sealed record InstallOptions(
    string AgentName,
    IReadOnlyList<string> AgentTags,
    IReadOnlyList<string> AllowedPeers,
    IReadOnlyList<string> BootstrapAddrs,
    string RendezvousGroup,
    int MaxParallelTasks,
    long MaxTransferBytes,
    string RunAsUser,
    string? SwarmKeySourcePath,
    bool HardenAcl);

public sealed record InstallStep(string Title, bool Success, string Detail);
public sealed record DeploymentIssue(string ResourceKey, string Detail = "");

/// <summary>
/// 被控端的事务化安装、修复与卸载。所有 payload 在停机前完成验证；写入密钥前完成
/// reparse-point 检查与完整 DACL 替换；失败时恢复文件与计划任务并尽力恢复原运行状态。
/// </summary>
public sealed class InstallerService
{
    private readonly AgentConfigService _config = new();
    private readonly ScheduledTaskService _task = new();

    public static bool IsInstalled =>
        File.Exists(AppPaths.AgentExe) && File.Exists(AppPaths.ConfigFile);

    public static async Task<IReadOnlyList<DeploymentIssue>> CheckDeploymentAsync(CancellationToken ct = default)
    {
        var issues = new List<DeploymentIssue>();
        if (!File.Exists(AppPaths.ControlExe))
        {
            issues.Add(new DeploymentIssue("DeploymentControlMissing"));
        }
        else
        {
            var running = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(running) && File.Exists(running) &&
                !await FilesHaveSameContentAsync(AppPaths.ControlExe, running, ct).ConfigureAwait(false))
                issues.Add(new DeploymentIssue("DeploymentControlHashMismatch"));
        }

        var service = new ScheduledTaskService();
        var inspection = await service.InspectAsync(ct).ConfigureAwait(false);
        if (inspection.QueryFailed)
        {
            issues.Add(new DeploymentIssue("DeploymentTaskQueryFailed", inspection.QueryError));
        }
        else if (!inspection.Exists)
        {
            issues.Add(new DeploymentIssue("DeploymentTaskMissing"));
        }
        else if (!inspection.MatchesExpectedDefinition)
        {
            issues.AddRange(inspection.Issues.Select(issue =>
                new DeploymentIssue("DeploymentTaskDefinition", issue)));
        }

        if (!File.Exists(AppPaths.AgentExe))
        {
            issues.Add(new DeploymentIssue("DeploymentAgentMissing"));
        }
        else
        {
            try
            {
                await RuntimeSecurityService.ValidateAgentPayloadAsync(AppPaths.AgentExe, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                issues.Add(new DeploymentIssue("DeploymentAgentIntegrity", ex.Message));
            }
        }

        if (!File.Exists(AppPaths.ConfigFile))
        {
            issues.Add(new DeploymentIssue("DeploymentConfigMissing"));
        }
        else
        {
            try
            {
                AgentConfigService.ValidateRuntimeBoundary(new AgentConfigService().Load());
            }
            catch (Exception ex)
            {
                issues.Add(new DeploymentIssue("DeploymentConfigInvalid", ex.Message));
            }
        }

        if (!File.Exists(AppPaths.SwarmKeyFile))
        {
            issues.Add(new DeploymentIssue("DeploymentSwarmMissing"));
        }
        else
        {
            try
            {
                RuntimeSecurityService.ValidateSwarmKey(AppPaths.SwarmKeyFile);
            }
            catch (Exception ex)
            {
                issues.Add(new DeploymentIssue("DeploymentSwarmInvalid", ex.Message));
            }
        }

        try
        {
            RuntimeSecurityService.ValidateSecureDataRootForWrite();
        }
        catch (Exception ex)
        {
            issues.Add(new DeploymentIssue("DeploymentDataSecurityInvalid", ex.Message));
        }

        return issues;
    }

    public async Task<IReadOnlyList<InstallStep>> RepairAsync(
        IProgress<InstallStep>? progress = null,
        CancellationToken ct = default)
    {
        var steps = new List<InstallStep>();
        InstallStep Record(string title, bool success, string detail)
        {
            var step = new InstallStep(title, success, detail);
            steps.Add(step);
            progress?.Report(step);
            return step;
        }

        var inspection = await _task.InspectAsync(ct).ConfigureAwait(false);
        if (inspection.QueryFailed)
        {
            Record("读取现有计划任务", false,
                "无法确认现有任务及运行账户，修复已安全中止：" + inspection.QueryError);
            return steps;
        }

        var runAsUser = inspection.Exists && inspection.MatchesExpectedDefinition &&
                        !string.IsNullOrWhiteSpace(inspection.RunAsUser)
            ? inspection.RunAsUser
            : global::ZhanClawControl.App.InteractiveUserName;
        if (string.IsNullOrWhiteSpace(runAsUser))
        {
            Record("确定运行账户", false,
                "计划任务不存在，无法推断原运行账户。请由调用方显式提供交互用户账户后再修复。");
            return steps;
        }
        // Never restore or restart a tampered same-name task during rollback.
        var previousTaskXml = inspection.Exists && inspection.MatchesExpectedDefinition ? inspection.RawXml : null;
        var previousState = await _task.GetStateAsync(ct).ConfigureAwait(false);
        var preserveDisabled = previousTaskXml is not null &&
                               !ScheduledTaskService.ReadTaskEnabled(previousTaskXml);
        var wasRunning = previousTaskXml is not null &&
                         (previousState == TaskState.Running || ScheduledTaskService.IsAgentProcessRunning());
        var oldAgentRunnable = IsTrustedRollbackAgent();

        string? stagedAgent = null;
        string? stagedControl = null;
        string? stagedSwarm = null;
        DeploymentBackup? backup = null;
        JsonObject repairConfig;
        var rebuildConfig = false;
        var rebuildSwarm = false;
        try
        {
            RuntimeSecurityService.EnsureSafeInstallRoot();
            RuntimeSecurityService.ValidateExistingDataRootTrust(runAsUser);
            stagedAgent = await StageAndValidateAgentAsync(ct).ConfigureAwait(false);
            stagedControl = StageControlExecutable();
            try
            {
                repairConfig = _config.Exists ? _config.Load() : throw new FileNotFoundException();
                AgentConfigService.ValidateRuntimeBoundary(repairConfig);
            }
            catch
            {
                repairConfig = AgentConfigService.CreateDefault();
                AgentConfigService.SetAllowedPeers(repairConfig, Array.Empty<string>());
                rebuildConfig = true;
            }
            try
            {
                if (!File.Exists(AppPaths.SwarmKeyFile)) throw new FileNotFoundException();
                RuntimeSecurityService.ValidateSwarmKey(AppPaths.SwarmKeyFile);
            }
            catch
            {
                stagedSwarm = StageSwarmKey(null);
                RuntimeSecurityService.ValidateSwarmKey(stagedSwarm);
                rebuildSwarm = true;
            }
            Record("验证安装载荷", true, "已审查载荷的 SHA-256、AMD64/Console PE、Authenticode 固定值与清单元数据均匹配");

            backup = CreateBackup(previousTaskXml, oldAgentRunnable, !rebuildConfig, !rebuildSwarm);
            var stop = await _task.StopAsync(ct).ConfigureAwait(false);
            if (!stop.Success) throw new InvalidOperationException("无法确认旧 Agent 已停止：" + stop.CombinedOutput);
            Record("停止后台进程", true, "已验证本产品宿主与 Agent 进程均退出");

            RuntimeSecurityService.PrepareSecureDataRoot(runAsUser);
            Record("校验目录与 ACL", true, $"停机后完成数据目录加固；运行账户保持为 {runAsUser}");

            AtomicReplace(stagedAgent, AppPaths.AgentExe);
            stagedAgent = null;
            DeployControlExecutable(stagedControl);
            stagedControl = null;
            if (stagedSwarm is not null)
            {
                AtomicReplace(stagedSwarm, AppPaths.SwarmKeyFile);
                stagedSwarm = null;
                Record("重建 swarm.key", true, "原文件缺失或无效，已使用安装包内置密钥重建");
            }
            if (rebuildConfig)
            {
                _config.Save(repairConfig);
                Record("重建 agent-config.json", true,
                    "原配置缺失或无效；已写入安全默认值和空 allowed_peers。未配置获准主控；最终请求策略由 Agent 决定");
            }
            CleanupLegacyLauncher();
            Record("更新程序文件", true, AppPaths.AgentExe);

            var register = await _task.RegisterAsync(runAsUser, ct).ConfigureAwait(false);
            if (!register.Success) throw new InvalidOperationException("重建计划任务失败：" + register.CombinedOutput);
            Record("重建开机自启任务", true, $"保留运行账户：{runAsUser}");

            var start = await _task.StartAsync(ct).ConfigureAwait(false);
            if (!start.Success) throw new InvalidOperationException("启动 Agent 失败：" + start.CombinedOutput);
            if (!await WaitForReadyAsync(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false))
                throw new TimeoutException("Agent 在 45 秒内未通过本机 API 就绪验证。");
            if (preserveDisabled)
            {
                var disable = await _task.SetEnabledAsync(false, ct).ConfigureAwait(false);
                if (!disable.Success) throw new InvalidOperationException("无法恢复原登录自启偏好：" + disable.CombinedOutput);
            }
            Record("启动并验证 Agent", true, $"{AppPaths.ApiHost}:{AppPaths.ApiPort} 已鉴权应答");

            if (!backup.Delete()) Record("清理回滚备份", false, "无法删除受保护备份：" + backup.RootPath);
            backup = null;
            return steps;
        }
        catch (Exception ex)
        {
            Record("修复安装", false, ex.Message);
            if (backup is not null)
            {
                var rollback = await RollbackAsync(backup, previousTaskXml, wasRunning && oldAgentRunnable).ConfigureAwait(false);
                Record("回滚", rollback.Success, rollback.Detail);
                if (rollback.Success)
                {
                    var deleted = backup.Delete();
                    Record("清理回滚备份", deleted, deleted ? "已删除" : "无法删除：" + backup.RootPath);
                    if (deleted) backup = null;
                }
            }

            return steps;
        }
        finally
        {
            TryDelete(stagedAgent);
            TryDelete(stagedControl);
            TryDelete(stagedSwarm);
            // 回滚失败时保留备份供人工恢复；失败步骤已报告其绝对路径。
        }
    }

    public static string CurrentUserName
    {
        get
        {
            try { return WindowsIdentity.GetCurrent().Name; }
            catch { return $@"{Environment.UserDomainName}\{Environment.UserName}"; }
        }
    }

    public static bool HasEmbeddedSwarmKey =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains(AppPaths.SwarmKeyPayloadResource);

    public async Task<IReadOnlyList<InstallStep>> InstallAsync(
        InstallOptions options,
        IProgress<InstallStep>? progress = null,
        CancellationToken ct = default)
    {
        var steps = new List<InstallStep>();
        InstallStep Record(string title, bool success, string detail)
        {
            var step = new InstallStep(title, success, detail);
            steps.Add(step);
            progress?.Report(step);
            return step;
        }

        string? stagedAgent = null;
        string? stagedControl = null;
        string? stagedSwarm = null;
        DeploymentBackup? backup = null;
        var previousInspection = await _task.InspectAsync(ct).ConfigureAwait(false);
        if (previousInspection.QueryFailed)
        {
            Record("读取现有计划任务", false,
                "无法确认现有任务状态，安装已安全中止：" + previousInspection.QueryError);
            return steps;
        }

        // A same-name task that fails our exact definition is replaced, never restored or run.
        var previousTaskXml = previousInspection.Exists && previousInspection.MatchesExpectedDefinition
            ? previousInspection.RawXml
            : null;
        var previousState = await _task.GetStateAsync(ct).ConfigureAwait(false);
        var preserveDisabled = previousTaskXml is not null &&
                               !ScheduledTaskService.ReadTaskEnabled(previousTaskXml);
        var wasRunning = previousTaskXml is not null &&
                         (previousState == TaskState.Running || ScheduledTaskService.IsAgentProcessRunning());
        var oldAgentRunnable = IsTrustedRollbackAgent();

        try
        {
            ValidateInstallOptions(options);
            RuntimeSecurityService.EnsureSafeInstallRoot();
            RuntimeSecurityService.ValidateExistingDataRootTrust(options.RunAsUser);
            stagedAgent = await StageAndValidateAgentAsync(ct).ConfigureAwait(false);
            stagedControl = StageControlExecutable();
            var keepExistingSwarm = false;
            if (File.Exists(AppPaths.SwarmKeyFile))
            {
                try
                {
                    RuntimeSecurityService.ValidateSwarmKey(AppPaths.SwarmKeyFile);
                    keepExistingSwarm = true;
                }
                catch { /* replace damaged existing key from selected/embedded trusted input */ }
            }
            if (!keepExistingSwarm)
            {
                stagedSwarm = StageSwarmKey(options.SwarmKeySourcePath);
                RuntimeSecurityService.ValidateSwarmKey(stagedSwarm);
            }
            Record("验证安装载荷", true, "Agent 完整性及 swarm.key 格式验证通过");

            backup = CreateBackup(previousTaskXml, oldAgentRunnable);
            var stop = await _task.StopAsync(ct).ConfigureAwait(false);
            if (!stop.Success) throw new InvalidOperationException("无法确认旧 Agent 已停止：" + stop.CombinedOutput);
            Record("停止已有 Agent 实例", true, "仅停止安装路径精确匹配的进程");

            RuntimeSecurityService.PrepareSecureDataRoot(options.RunAsUser);
            Directory.CreateDirectory(AppPaths.LogDirectory);
            Record("创建并保护安装目录", true,
                options.HardenAcl
                    ? "停机后已将数据目录完整 DACL 替换为运行账户、Administrators 与 SYSTEM"
                    : "安全边界要求强制启用完整 DACL；未采用跳过 ACL 的请求");

            AtomicReplace(stagedAgent, AppPaths.AgentExe);
            stagedAgent = null;
            DeployControlExecutable(stagedControl);
            stagedControl = null;
            if (stagedSwarm is not null)
            {
                AtomicReplace(stagedSwarm, AppPaths.SwarmKeyFile);
                stagedSwarm = null;
                Record("写入 swarm.key", true, HasEmbeddedSwarmKey ? "来源：安装包内置" : "来源：用户选择的文件");
            }
            else
            {
                Record("写入 swarm.key", true, "保留并验证本机已有的 swarm.key");
            }
            CleanupLegacyLauncher();
            Record("部署程序文件", true, AppPaths.AgentExe);

            JsonObject config;
            try { config = _config.Exists ? _config.Load() : AgentConfigService.CreateDefault(); }
            catch { config = AgentConfigService.CreateDefault(); }
            config["agent_name"] = options.AgentName;
            AgentConfigService.SetStringArray(config, "agent_tags", options.AgentTags);
            AgentConfigService.SetStringArray(config, "bootstrap_addrs", options.BootstrapAddrs);
            AgentConfigService.SetAllowedPeers(config, options.AllowedPeers);
            config["swarm_key"] = AgentConfigService.ToJsonPath(AppPaths.SwarmKeyFile);
            config["identity_file"] = AgentConfigService.ToJsonPath(AppPaths.IdentityFile);
            config["api_token_file"] = AgentConfigService.ToJsonPath(AppPaths.ApiTokenFile);
            config["command_journal_file"] = AgentConfigService.ToJsonPath(AppPaths.JournalFile);
            config["api_listen"] = $"{AppPaths.ApiHost}:{AppPaths.ApiPort}";
            config["rendezvous_group"] = options.RendezvousGroup;
            config["max_parallel_tasks"] = options.MaxParallelTasks;
            config["max_transfer_bytes"] = options.MaxTransferBytes;
            _config.Save(config);
            Record("写入 agent-config.json", true,
                options.AllowedPeers.Count == 0
                    ? "已写入空 allowed_peers，未配置获准主控；最终请求策略由 Agent 决定"
                    : $"已授权 {options.AllowedPeers.Count} 个主控 PeerID");

            var register = await _task.RegisterAsync(options.RunAsUser, ct).ConfigureAwait(false);
            if (!register.Success) throw new InvalidOperationException("注册计划任务失败：" + register.CombinedOutput);
            Record("注册开机自启任务", true, $"运行账户：{options.RunAsUser}");

            var start = await _task.StartAsync(ct).ConfigureAwait(false);
            if (!start.Success) throw new InvalidOperationException("启动 Agent 失败：" + start.CombinedOutput);
            if (!await WaitForReadyAsync(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false))
                throw new TimeoutException("Agent 在 45 秒内未通过本机 API 就绪验证。");
            if (preserveDisabled)
            {
                var disable = await _task.SetEnabledAsync(false, ct).ConfigureAwait(false);
                if (!disable.Success) throw new InvalidOperationException("无法恢复原登录自启偏好：" + disable.CombinedOutput);
            }
            Record("启动并验证 Agent", true, $"{AppPaths.ApiHost}:{AppPaths.ApiPort} 已鉴权应答");

            if (!backup.Delete()) Record("清理回滚备份", false, "无法删除受保护备份：" + backup.RootPath);
            backup = null;
            return steps;
        }
        catch (Exception ex)
        {
            Record("安装中断", false, ex.Message);
            if (backup is not null)
            {
                var rollback = await RollbackAsync(backup, previousTaskXml, wasRunning && oldAgentRunnable).ConfigureAwait(false);
                Record("回滚", rollback.Success, rollback.Detail);
                if (rollback.Success)
                {
                    var deleted = backup.Delete();
                    Record("清理回滚备份", deleted, deleted ? "已删除" : "无法删除：" + backup.RootPath);
                    if (deleted) backup = null;
                }
            }

            return steps;
        }
        finally
        {
            TryDelete(stagedAgent);
            TryDelete(stagedControl);
            TryDelete(stagedSwarm);
            // 回滚失败时保留备份供人工恢复；失败步骤已报告其绝对路径。
        }
    }

    public static async Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (ScheduledTaskService.IsAgentProcessRunning() &&
                await ControlApiClient.IsPortOpenAsync(500, ct).ConfigureAwait(false) &&
                File.Exists(AppPaths.ApiTokenFile))
            {
                using var client = new ControlApiClient();
                if (await client.GetInfoAsync(ct).ConfigureAwait(false) is not null) return true;
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>供授权页、设置页改造使用：每一步均验证，失败抛错，禁止 UI 误报重启成功。</summary>
    public static async Task RestartVerifiedAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var task = new ScheduledTaskService();
        var stop = await task.StopAsync(ct).ConfigureAwait(false);
        if (!stop.Success) throw new InvalidOperationException("无法确认 Agent 已停止：" + stop.CombinedOutput);
        var start = await task.StartAsync(ct).ConfigureAwait(false);
        if (!start.Success) throw new InvalidOperationException("无法确认 Agent 已启动：" + start.CombinedOutput);
        if (!await WaitForReadyAsync(timeout, ct).ConfigureAwait(false))
            throw new TimeoutException($"Agent 未在 {timeout.TotalSeconds:0} 秒内通过鉴权就绪验证。");
    }

    public async Task<IReadOnlyList<InstallStep>> UninstallAsync(bool removeData, CancellationToken ct = default)
    {
        var steps = new List<InstallStep>();
        var inspection = await _task.InspectAsync(ct).ConfigureAwait(false);
        if (inspection.QueryFailed)
            throw new InvalidOperationException("卸载已中止：无法确认计划任务状态。" + inspection.QueryError);
        if (inspection.Exists && !inspection.MatchesExpectedDefinition)
            throw new InvalidOperationException("卸载已中止：同名计划任务未通过精确定义校验，拒绝在事务中恢复或启动它。" +
                                                string.Join("；", inspection.Issues));
        string? originalDataUserSid = null;
        if (removeData && Directory.Exists(AppPaths.DataRoot))
            originalDataUserSid = RuntimeSecurityService.ResolveProtectedDataRootUserSid();

        var taskXml = inspection.Exists ? inspection.RawXml : null;
        var taskEnabled = taskXml is not null && ScheduledTaskService.ReadTaskEnabled(taskXml);
        var state = await _task.GetStateAsync(ct).ConfigureAwait(false);
        var wasRunning = taskXml is not null &&
                         (state == TaskState.Running || ScheduledTaskService.IsAgentProcessRunning());
        var trustedAgent = IsTrustedRollbackAgent();
        if (wasRunning && !trustedAgent)
            throw new InvalidOperationException("卸载已中止：原 Agent 正在运行但不能证明其为可信发布者，无法安全满足失败回滚后的健康恢复要求。");

        RuntimeSecurityService.EnsureSafeInstallRoot();
        DeploymentBackup? backup = null;
        string? isolatedData = null;
        var committed = false;
        try
        {
            backup = CreateBackup(taskXml, trustedAgent);
            steps.Add(new InstallStep("创建回滚点", true, backup.RootPath));

            var stop = await _task.StopAsync(ct).ConfigureAwait(false);
            if (!stop.Success) throw new InvalidOperationException("无法确认 Agent 停止：" + stop.CombinedOutput);
            steps.Add(new InstallStep("停止 Agent", true, "已验证本产品宿主与 Agent 进程退出"));

            var delete = await _task.DeleteAsync(ct).ConfigureAwait(false);
            if (!delete.Success) throw new InvalidOperationException("无法删除计划任务：" + delete.CombinedOutput);
            var verifyDeleted = await _task.InspectAsync(ct).ConfigureAwait(false);
            if (verifyDeleted.QueryFailed || verifyDeleted.Exists)
                throw new InvalidOperationException("计划任务删除后复核失败：" + verifyDeleted.QueryError);
            steps.Add(new InstallStep("隔离计划任务", true, inspection.Exists ? "已删除并复核不存在" : "任务原本不存在"));

            if (removeData && Directory.Exists(AppPaths.DataRoot))
            {
                RuntimeSecurityService.RejectReparsePoint(AppPaths.DataRoot);
                isolatedData = Path.Combine(AppPaths.InstallRoot, $".uninstall-data-{Guid.NewGuid():N}");
                Directory.Move(AppPaths.DataRoot, isolatedData);
                RuntimeSecurityService.ProtectRollbackTree(isolatedData);
                if (Directory.Exists(AppPaths.DataRoot) || !Directory.Exists(isolatedData))
                    throw new IOException("运行数据隔离后的路径复核失败。");
                steps.Add(new InstallStep("隔离运行数据", true, isolatedData));
            }

            TryDeleteRequired(AppPaths.AgentExe);
            var deferredSelfDelete = Environment.ProcessPath is { } current && PathsEqual(current, AppPaths.ControlExe);
            if (!deferredSelfDelete) TryDeleteRequired(AppPaths.ControlExe);
            CleanupLegacyLauncher();
            steps.Add(new InstallStep("移除程序文件", true,
                deferredSelfDelete ? "Agent 已移除；控制程序等待 Windows 重启清理" : "程序文件已移除"));

            // This is the last fallible mutation before commit. Once Windows accepts delayed deletion,
            // no later cleanup failure is reported as a transaction failure requiring restoration.
            if (deferredSelfDelete)
            {
                var cleanupDetail = ScheduleSelfDelete();
                steps.Add(new InstallStep("安排重启后清理", true, cleanupDetail));
            }
            committed = true;

            if (isolatedData is not null)
            {
                try
                {
                    Directory.Delete(isolatedData, recursive: true);
                    steps.Add(new InstallStep("清理隔离数据", true, "已删除设备身份、配置与任务记录"));
                    isolatedData = null;
                }
                catch (Exception ex)
                {
                    // Functional uninstall is committed. The BA/SY-only quarantine remains inaccessible
                    // to the former runAs user and its exact path is reported for maintenance cleanup.
                    steps.Add(new InstallStep("清理隔离数据", false, $"安全隔离数据仍保留：{isolatedData}；{ex.Message}"));
                }
            }
            else if (!removeData)
                steps.Add(new InstallStep("保留运行数据", true, $"设备身份与配置保留在 {AppPaths.DataRoot}"));

            if (backup.Delete()) backup = null;
            else steps.Add(new InstallStep("清理回滚点", false, "受保护回滚点仍保留：" + backup.RootPath));
            TryDeleteEmptyDirectory(AppPaths.InstallRoot);
            return steps;
        }
        catch (Exception uninstallError)
        {
            if (committed || backup is null) throw;
            using var rollbackCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var rollbackErrors = new List<string>();
            try
            {
                var stop = await _task.StopAsync(rollbackCts.Token).ConfigureAwait(false);
                if (!stop.Success) rollbackErrors.Add("回滚停机未确认：" + stop.CombinedOutput);
            }
            catch (Exception ex) { rollbackErrors.Add("回滚停机未确认：" + ex.Message); }

            if (rollbackErrors.Count == 0)
            {
                try
                {
                    if (isolatedData is not null && Directory.Exists(isolatedData))
                    {
                        if (Directory.Exists(AppPaths.DataRoot)) throw new IOException("DataRoot 已被其他对象占用。");
                        Directory.Move(isolatedData, AppPaths.DataRoot);
                        isolatedData = null;
                        RuntimeSecurityService.RestoreDataRootFromProtectedQuarantine(
                            originalDataUserSid ?? throw new InvalidOperationException("缺少 DataRoot ACL 用户快照。"));
                    }
                    await backup.RestoreAsync(taskXml is not null && trustedAgent, rollbackCts.Token).ConfigureAwait(false);
                    if (taskXml is not null)
                    {
                        var register = await _task.RegisterXmlAsync(taskXml, rollbackCts.Token).ConfigureAwait(false);
                        if (!register.Success) throw new InvalidOperationException(register.CombinedOutput);
                        var restored = await _task.InspectAsync(rollbackCts.Token).ConfigureAwait(false);
                        if (restored.QueryFailed || !restored.Exists || !restored.MatchesExpectedDefinition ||
                            ScheduledTaskService.ReadTaskEnabled(restored.RawXml) != taskEnabled)
                            throw new InvalidOperationException("原任务定义或 Enabled 状态恢复复核失败。");
                    }
                    if (wasRunning)
                    {
                        var start = await _task.StartAsync(rollbackCts.Token).ConfigureAwait(false);
                        if (!start.Success || !await WaitForReadyAsync(TimeSpan.FromSeconds(30), rollbackCts.Token).ConfigureAwait(false))
                            throw new InvalidOperationException("原 Agent 未通过恢复启动及鉴权 API 健康验证。");
                    }
                }
                catch (Exception ex) { rollbackErrors.Add(ex.Message); }
            }

            if (rollbackErrors.Count == 0)
            {
                steps.Add(new InstallStep("卸载回滚", true, "已恢复文件、任务、Enabled 偏好及原运行状态并完成健康验证"));
                if (backup.Delete()) backup = null;
            }
            else
            {
                try { await _task.StopAsync(rollbackCts.Token).ConfigureAwait(false); } catch { }
                steps.Add(new InstallStep("卸载回滚", false,
                    string.Join("；", rollbackErrors) + $"；受保护回滚点保留：{backup.RootPath}"));
            }
            throw new InvalidOperationException("卸载事务失败：" + uninstallError.Message + "；" +
                                                steps.Last(step => step.Title == "卸载回滚").Detail, uninstallError);
        }
    }

    private static void ValidateInstallOptions(InstallOptions options)
    {
        RuntimeSecurityService.ResolveAccountSid(options.RunAsUser);
        var agentName = options.AgentName.Trim();
        if (agentName.Length is < 1 or > 128 || agentName.Any(char.IsControl))
            throw new InvalidDataException("Agent 名称必须为 1–128 个非控制字符。");
        if (options.MaxParallelTasks is < 1 or > 64) throw new InvalidDataException("并行任务数必须为 1–64。");
        if (options.MaxTransferBytes is < 1 or > (1L << 40))
            throw new InvalidDataException("传输上限必须为 1 字节至 1 TiB。");
        if (string.IsNullOrWhiteSpace(options.RendezvousGroup) ||
            options.RendezvousGroup.Length > 128 ||
            options.RendezvousGroup.Any(char.IsWhiteSpace))
            throw new InvalidDataException("发现组不能为空、不能含空白，且最长 128 字符。");
        if (options.BootstrapAddrs.Count == 0 || options.BootstrapAddrs.Count > 32 ||
            options.BootstrapAddrs.Any(address => !LooksLikeBootstrapMultiaddr(address)))
            throw new InvalidDataException("bootstrap_addrs 为空、数量过多或包含无效 libp2p multiaddr。");
        var probe = new JsonObject();
        AgentConfigService.SetAllowedPeers(probe, options.AllowedPeers);
    }

    private static bool LooksLikeBootstrapMultiaddr(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsWhiteSpace) ||
            !value.StartsWith("/", StringComparison.Ordinal)) return false;
        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6 || !parts.Contains("p2p", StringComparer.Ordinal)) return false;
        var p2p = Array.LastIndexOf(parts, "p2p");
        return p2p >= 0 && p2p + 1 < parts.Length && AgentConfigService.IsValidPeerId(parts[p2p + 1]);
    }

    private static async Task<string> StageAndValidateAgentAsync(CancellationToken ct)
    {
        var stage = Path.Combine(AppPaths.InstallRoot, $"p2p-agent.exe.{Guid.NewGuid():N}.stage.exe");
        ExtractResource(AppPaths.AgentPayloadResource, stage);
        try
        {
            await RuntimeSecurityService.ValidateAgentPayloadAsync(stage, ct).ConfigureAwait(false);
            return stage;
        }
        catch
        {
            TryDelete(stage);
            throw;
        }
    }

    private static string StageControlExecutable()
    {
        var source = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            throw new FileNotFoundException("无法定位控制软件自身路径。");

        RuntimeSecurityService.RejectReparsePoint(source);
        var stage = Path.Combine(AppPaths.InstallRoot, $"ZhanClawControl.{Guid.NewGuid():N}.stage.exe");
        File.Copy(source, stage, overwrite: false);
        return stage;
    }

    private static string StageSwarmKey(string? selectedPath)
    {
        var stage = Path.Combine(AppPaths.InstallRoot, $"swarm.key.{Guid.NewGuid():N}.stage");
        if (HasEmbeddedSwarmKey)
        {
            ExtractResource(AppPaths.SwarmKeyPayloadResource, stage);
        }
        else if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            RuntimeSecurityService.RejectReparsePoint(selectedPath);
            File.Copy(selectedPath, stage, overwrite: false);
        }
        else
        {
            throw new FileNotFoundException("没有已有或可用的 swarm.key。");
        }

        return stage;
    }

    private static void ExtractResource(string resourceName, string targetPath)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                           ?? throw new FileNotFoundException($"安装包缺少嵌入资源：{resourceName}");
        using var file = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.CopyTo(file);
        file.Flush(flushToDisk: true);
    }

    private static void AtomicReplace(string source, string target)
    {
        RuntimeSecurityService.RejectReparsePoint(source);
        if (File.Exists(target)) RuntimeSecurityService.RejectReparsePoint(target);
        File.Move(source, target, overwrite: true);
    }

    private static void DeployControlExecutable(string stagedControl)
    {
        if (File.Exists(AppPaths.ControlExe) && FilesHaveSameContent(stagedControl, AppPaths.ControlExe))
        {
            TryDelete(stagedControl);
            return;
        }

        if (Environment.ProcessPath is { } current && PathsEqual(current, AppPaths.ControlExe))
            throw new IOException("当前控制程序正从安装目录运行且内容需要更新，不能安全覆盖已映射的 EXE。请从新安装包副本执行修复。");
        AtomicReplace(stagedControl, AppPaths.ControlExe);
    }

    private static bool PathsEqual(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static bool FilesHaveSameContent(string left, string right)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
        using var a = File.OpenRead(left); using var b = File.OpenRead(right);
        return SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
    }

    private static async Task<bool> FilesHaveSameContentAsync(string left, string right, CancellationToken ct)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
        await using var a = File.OpenRead(left); await using var b = File.OpenRead(right);
        var ah = await SHA256.HashDataAsync(a, ct).ConfigureAwait(false);
        var bh = await SHA256.HashDataAsync(b, ct).ConfigureAwait(false);
        return ah.AsSpan().SequenceEqual(bh);
    }

    private static void CleanupLegacyLauncher()
    {
        try
        {
            if (File.Exists(AppPaths.LegacyLauncherCmd))
            {
                RuntimeSecurityService.RejectReparsePoint(AppPaths.LegacyLauncherCmd);
                File.Delete(AppPaths.LegacyLauncherCmd);
            }
        }
        catch
        {
            // 旧启动器残留不改变当前任务动作；健康检查会校验实际任务 XML。
        }
    }

    private static bool IsTrustedRollbackAgent()
    {
        if (!File.Exists(AppPaths.AgentExe)) return false;
        try
        {
            RuntimeSecurityService.ValidateTrustedAgentPublisherForRollback(AppPaths.AgentExe);
            return true;
        }
        catch { return false; }
    }

    private static DeploymentBackup CreateBackup(
        string? taskXml,
        bool includeRunnableAgent = true,
        bool includeConfig = true,
        bool includeSwarm = true)
    {
        var backup = new DeploymentBackup(taskXml);
        try
        {
            if (includeRunnableAgent) backup.Capture(AppPaths.AgentExe);
            else backup.MarkAbsent(AppPaths.AgentExe);
            backup.Capture(AppPaths.ControlExe);
            if (includeConfig) backup.Capture(AppPaths.ConfigFile); else backup.MarkAbsent(AppPaths.ConfigFile);
            if (includeSwarm) backup.Capture(AppPaths.SwarmKeyFile); else backup.MarkAbsent(AppPaths.SwarmKeyFile);
            return backup;
        }
        catch (Exception captureError)
        {
            if (!backup.Delete())
                throw new IOException($"创建回滚点失败且无法清理敏感备份：{backup.RootPath}", captureError);
            throw;
        }
    }

    private async Task<(bool Success, string Detail)> RollbackAsync(
        DeploymentBackup backup,
        string? previousTaskXml,
        bool wasRunning)
    {
        var errors = new List<string>();
        using var rollbackCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = rollbackCts.Token;
        try
        {
            var stop = await _task.StopAsync(ct).ConfigureAwait(false);
            if (!stop.Success)
                return (false, "回滚停机未确认，禁止覆盖任何文件：" + stop.CombinedOutput +
                               $"；回滚备份已保留：{backup.RootPath}");
        }
        catch (Exception ex)
        {
            return (false, "回滚停机未确认，禁止覆盖任何文件：" + ex.Message +
                           $"；回滚备份已保留：{backup.RootPath}");
        }
        try { await backup.RestoreAsync(previousTaskXml is not null, ct).ConfigureAwait(false); }
        catch (Exception ex) { errors.Add("文件恢复或恢复后完整性校验失败：" + ex.Message); }
        try
        {
            if (errors.Count > 0 || previousTaskXml is null)
            {
                var delete = await _task.DeleteAsync(ct).ConfigureAwait(false);
                if (!delete.Success) errors.Add("删除新任务失败：" + delete.CombinedOutput);
                var deleted = await _task.InspectAsync(ct).ConfigureAwait(false);
                if (deleted.QueryFailed || deleted.Exists) errors.Add("删除新任务后的复核失败：" + deleted.QueryError);
            }
            else
            {
                var restoreTask = await _task.RegisterXmlAsync(previousTaskXml, ct).ConfigureAwait(false);
                if (!restoreTask.Success) errors.Add("任务恢复失败：" + restoreTask.CombinedOutput);
                var restored = await _task.InspectAsync(ct).ConfigureAwait(false);
                if (restored.QueryFailed || !restored.Exists || !restored.MatchesExpectedDefinition)
                    errors.Add("任务恢复后的精确定义复核失败：" +
                               string.Join("；", restored.Issues.Append(restored.QueryError).Where(x => !string.IsNullOrWhiteSpace(x))));
                if (errors.Count > 0)
                {
                    var failClosed = await _task.DeleteAsync(ct).ConfigureAwait(false);
                    if (!failClosed.Success) errors.Add("恢复校验失败后删除任务也失败：" + failClosed.CombinedOutput);
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add("任务恢复失败：" + ex.Message);
        }

        if (wasRunning && previousTaskXml is not null && errors.Count == 0)
        {
            try
            {
                var start = await _task.StartAsync(ct).ConfigureAwait(false);
                if (!start.Success) errors.Add("原 Agent 恢复启动失败：" + start.CombinedOutput);
                else if (!await WaitForReadyAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false))
                {
                    errors.Add("原 Agent 已恢复启动，但未在 30 秒内通过本机鉴权 API 健康验证。");
                    var stopUnhealthy = await _task.StopAsync(ct).ConfigureAwait(false);
                    if (!stopUnhealthy.Success)
                        errors.Add("健康验证失败后的停机也未确认：" + stopUnhealthy.CombinedOutput);
                }
            }
            catch (Exception ex)
            {
                errors.Add("原 Agent 恢复启动失败：" + ex.Message);
            }
        }

        return errors.Count == 0
            ? (true, "已恢复原程序文件、配置、计划任务与运行状态")
            : (false, string.Join("；", errors) + $"；回滚备份已保留：{backup.RootPath}");
    }

    private static string ScheduleSelfDelete()
    {
        // No executable content is placed in %TEMP%. Windows performs the deletion at next restart.
        if (!MoveFileEx(AppPaths.ControlExe, null, 0x00000004))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法安排控制程序在 Windows 重启后删除。");
        MoveFileEx(AppPaths.InstallRoot, null, 0x00000004); // best effort once the file has gone
        return $"{AppPaths.ControlExe} 已由 Windows 安排在下次重启时删除";
    }

    private static void TryDeleteRequired(string path)
    {
        if (!File.Exists(path)) return;
        RuntimeSecurityService.RejectReparsePoint(path);
        File.Delete(path);
        if (File.Exists(path)) throw new IOException("文件删除后仍存在：" + path);
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch { /* 报告文件已删除即可，非空目录留给维护者检查 */ }
    }

    private sealed class DeploymentBackup
    {
        private readonly string _root;
        private sealed record BackupEntry(string Path, string Sha256);
        private readonly Dictionary<string, BackupEntry?> _files = new(StringComparer.OrdinalIgnoreCase);
        public string? TaskXml { get; }
        public string RootPath => _root;

        public DeploymentBackup(string? taskXml)
        {
            TaskXml = taskXml;
            _root = Path.Combine(AppPaths.InstallRoot, $".install-rollback-{Guid.NewGuid():N}");
            RuntimeSecurityService.PrepareSecureRollbackDirectory(_root);
        }

        public void Capture(string path)
        {
            if (!File.Exists(path))
            {
                _files[path] = null;
                return;
            }

            RuntimeSecurityService.RejectReparsePoint(path);
            var backupPath = Path.Combine(_root, _files.Count.ToString("D2") + ".bak");
            File.Copy(path, backupPath, overwrite: false);
            var hash = HashFile(backupPath);
            _files[path] = new BackupEntry(backupPath, hash);
        }

        public void MarkAbsent(string path) => _files[path] = null;

        public Task RestoreAsync(bool requireRunnableDeployment, CancellationToken ct)
        {
            foreach (var (target, entry) in _files)
            {
                ct.ThrowIfCancellationRequested();
                if (entry is null)
                {
                    if (File.Exists(target))
                    {
                        RuntimeSecurityService.RejectReparsePoint(target);
                        File.Delete(target);
                        if (File.Exists(target)) throw new IOException("无法删除事务中新建的文件：" + target);
                    }
                }
                else
                {
                    RuntimeSecurityService.RejectReparsePoint(entry.Path);
                    if (!string.Equals(HashFile(entry.Path), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("回滚备份在恢复前哈希不匹配：" + entry.Path);
                    if (File.Exists(target)) RuntimeSecurityService.RejectReparsePoint(target);
                    File.Copy(entry.Path, target, overwrite: true);
                    if (!string.Equals(HashFile(target), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("恢复后的文件哈希不匹配：" + target);
                }
            }

            // Never restart from merely copied bytes. Reapply the same payload/config/key gates used
            // for a normal launch; any failure leaves the protected backup for manual recovery.
            if (File.Exists(AppPaths.AgentExe))
                RuntimeSecurityService.ValidateTrustedAgentPublisherForRollback(AppPaths.AgentExe);
            if (File.Exists(AppPaths.ConfigFile))
                AgentConfigService.ValidateRuntimeBoundary(new AgentConfigService().Load());
            if (File.Exists(AppPaths.SwarmKeyFile)) RuntimeSecurityService.ValidateSwarmKey(AppPaths.SwarmKeyFile);
            if (requireRunnableDeployment &&
                (!File.Exists(AppPaths.AgentExe) || !File.Exists(AppPaths.ControlExe) ||
                 !File.Exists(AppPaths.ConfigFile) || !File.Exists(AppPaths.SwarmKeyFile)))
                throw new InvalidDataException("原计划任务存在，但恢复后的可运行部署不完整。");
            return Task.CompletedTask;
        }

        private static string HashFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        public bool Delete()
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
                return !Directory.Exists(_root);
            }
            catch { return false; }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, uint flags);
}
