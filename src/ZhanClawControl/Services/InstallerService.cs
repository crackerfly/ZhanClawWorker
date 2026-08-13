using System.IO;
using System.Reflection;
using System.Security.Principal;
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

/// <summary>
/// 完整替代 02-install-worker.cmd 的安装流程。
/// 每一步返回结构化结果，向导逐条显示，失败即停止，不静默继续。
/// </summary>
public sealed class InstallerService
{
    private readonly AgentConfigService _config = new();
    private readonly ScheduledTaskService _task = new();

    public static bool IsInstalled =>
        File.Exists(AppPaths.AgentExe) && File.Exists(AppPaths.ConfigFile);

    public static string CurrentUserName
    {
        get
        {
            try
            {
                return WindowsIdentity.GetCurrent().Name;
            }
            catch
            {
                return $@"{Environment.UserDomainName}\{Environment.UserName}";
            }
        }
    }

    public static bool HasEmbeddedSwarmKey =>
        Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .Contains(AppPaths.SwarmKeyPayloadResource);

    public async Task<IReadOnlyList<InstallStep>> InstallAsync(
        InstallOptions options,
        IProgress<InstallStep>? progress = null,
        CancellationToken ct = default)
    {
        var steps = new List<InstallStep>();

        InstallStep Record(string title, bool ok, string detail)
        {
            var step = new InstallStep(title, ok, detail);
            steps.Add(step);
            progress?.Report(step);
            return step;
        }

        // 1. 目录
        try
        {
            Directory.CreateDirectory(AppPaths.InstallRoot);
            Directory.CreateDirectory(AppPaths.DataRoot);
            Directory.CreateDirectory(AppPaths.LogDirectory);
            Record("创建安装目录", true, $"{AppPaths.InstallRoot}；{AppPaths.DataRoot}");
        }
        catch (Exception ex)
        {
            Record("创建安装目录", false, ex.Message);
            return steps;
        }

        // 2. 停止可能在跑的旧实例，否则文件被占用无法覆盖
        try
        {
            if (await _task.GetStateAsync(ct).ConfigureAwait(false) != TaskState.NotInstalled)
            {
                await _task.StopAsync(ct).ConfigureAwait(false);
            }
            else
            {
                ScheduledTaskService.KillAgentProcesses();
            }

            await Task.Delay(1200, ct).ConfigureAwait(false);
            Record("停止已有 Agent 实例", true, "已确保目标文件未被占用");
        }
        catch (Exception ex)
        {
            Record("停止已有 Agent 实例", false, ex.Message);
            return steps;
        }

        // 3. 释放 p2p-agent.exe
        try
        {
            ExtractResource(AppPaths.AgentPayloadResource, AppPaths.AgentExe);
            var size = new FileInfo(AppPaths.AgentExe).Length;
            Record("释放 p2p-agent.exe", true, $"{AppPaths.AgentExe}（{size / 1024 / 1024} MB）");
        }
        catch (Exception ex)
        {
            Record("释放 p2p-agent.exe", false, ex.Message);
            return steps;
        }

        // 4. swarm.key
        try
        {
            if (HasEmbeddedSwarmKey)
            {
                // 内置密钥优先：保证同一批安装包产出的设备一定在同一私有网络
                ExtractResource(AppPaths.SwarmKeyPayloadResource, AppPaths.SwarmKeyFile);
                Record("写入 swarm.key", true, "来源：安装包内置");
            }
            else if (!string.IsNullOrWhiteSpace(options.SwarmKeySourcePath))
            {
                File.Copy(options.SwarmKeySourcePath!, AppPaths.SwarmKeyFile, overwrite: true);
                Record("写入 swarm.key", true, "来源：用户选择的文件");
            }
            else if (File.Exists(AppPaths.SwarmKeyFile))
            {
                Record("写入 swarm.key", true, "保留已存在的 swarm.key");
            }
            else
            {
                Record("写入 swarm.key", false, "未提供 swarm.key，Agent 无法加入私有网络");
                return steps;
            }
        }
        catch (Exception ex)
        {
            Record("写入 swarm.key", false, ex.Message);
            return steps;
        }

        // 5. 把控制软件自身复制到程序目录：计划任务执行的就是这个副本（--run-agent 宿主模式）
        try
        {
            InstallControlExecutable();
            CleanupLegacyLauncher();
            Record("安装后台宿主", true, $"{AppPaths.ControlExe} {AgentHost.Switch}");
        }
        catch (Exception ex)
        {
            Record("安装后台宿主", false, ex.Message);
            return steps;
        }

        // 6. 配置
        try
        {
            var config = _config.Exists ? _config.Load() : AgentConfigService.CreateDefault();

            config["agent_name"] = options.AgentName;
            AgentConfigService.SetStringArray(config, "agent_tags", options.AgentTags);
            AgentConfigService.SetStringArray(config, "bootstrap_addrs", options.BootstrapAddrs);
            AgentConfigService.SetStringArray(config, "allowed_peers", options.AllowedPeers);
            config["swarm_key"] = AgentConfigService.ToJsonPath(AppPaths.SwarmKeyFile);
            config["identity_file"] = AgentConfigService.ToJsonPath(AppPaths.IdentityFile);
            config["api_token_file"] = AgentConfigService.ToJsonPath(AppPaths.ApiTokenFile);
            config["command_journal_file"] = AgentConfigService.ToJsonPath(AppPaths.JournalFile);
            config["api_listen"] = $"{AppPaths.ApiHost}:{AppPaths.ApiPort}";
            config["rendezvous_group"] = options.RendezvousGroup;
            config["max_parallel_tasks"] = options.MaxParallelTasks;
            config["max_transfer_bytes"] = options.MaxTransferBytes;

            _config.Save(config);

            var peerCount = options.AllowedPeers.Count;
            var detail = peerCount == 0
                ? "已写入；allowed_peers 为空，当前拒绝所有远端操作"
                : $"已写入；已授权 {peerCount} 个主控 PeerID";
            Record("写入 agent-config.json", true, detail);
        }
        catch (Exception ex)
        {
            Record("写入 agent-config.json", false, ex.Message);
            return steps;
        }

        // 7. ACL 加固
        if (options.HardenAcl)
        {
            var acl = await ProcessRunner.RunAsync(
                ProcessRunner.SystemPath("icacls.exe"),
                new[]
                {
                    AppPaths.DataRoot,
                    "/inheritance:r",
                    "/grant:r", $"{options.RunAsUser}:(OI)(CI)F",
                    "/grant:r", "*S-1-5-32-544:(OI)(CI)F",
                    "/grant:r", "*S-1-5-18:(OI)(CI)F"
                },
                60_000,
                ct).ConfigureAwait(false);

            Record("收紧运行数据目录 ACL",
                acl.Success,
                acl.Success ? "仅当前用户、Administrators、SYSTEM 可访问" : acl.CombinedOutput);

            if (!acl.Success)
            {
                return steps;
            }
        }
        else
        {
            Record("收紧运行数据目录 ACL", true, "已按用户选择跳过");
        }

        // 8. 注册计划任务
        var register = await _task.RegisterAsync(options.RunAsUser, ct).ConfigureAwait(false);
        Record("注册开机自启任务",
            register.Success,
            register.Success ? $"任务名：{AppPaths.ScheduledTaskName}，运行账户：{options.RunAsUser}" : register.CombinedOutput);
        if (!register.Success)
        {
            return steps;
        }

        // 9. 启动并等待就绪
        var start = await _task.StartAsync(ct).ConfigureAwait(false);
        if (!start.Success)
        {
            Record("启动 Agent", false, start.CombinedOutput);
            return steps;
        }

        var ready = await WaitForReadyAsync(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
        Record("等待 Agent 就绪",
            ready,
            ready
                ? $"{AppPaths.ApiHost}:{AppPaths.ApiPort} 已监听"
                : "45 秒内未监听回环端口，请到「日志」页查看 agent.log");

        return steps;
    }

    public static async Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (ControlApiClient.IsPortOpen() && File.Exists(AppPaths.ApiTokenFile))
            {
                using var client = new ControlApiClient();
                var info = await client.GetInfoAsync(ct).ConfigureAwait(false);

                // 判定标准是「本机 API 能正常应答」，而不是「能解析出 PeerID」。
                // /v1/info 的字段名未在上游文档中固定，若解析不到 PeerID 也只是界面显示为空，
                // 不应据此判定 Agent 没起来。
                if (info is not null)
                {
                    return true;
                }
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// 把当前运行的 ZhanClawControl.exe 复制到程序目录。
    /// 计划任务需要一个稳定路径，用户下载目录里的原始文件随时可能被移动或删除。
    /// </summary>
    private static void InstallControlExecutable()
    {
        var source = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            throw new FileNotFoundException("无法定位控制软件自身路径。");
        }

        if (string.Equals(source, AppPaths.ControlExe, StringComparison.OrdinalIgnoreCase))
        {
            // 已经是从安装目录运行，无需复制
            return;
        }

        var tempPath = AppPaths.ControlExe + ".tmp";
        File.Copy(source, tempPath, overwrite: true);
        File.Move(tempPath, AppPaths.ControlExe, overwrite: true);
    }

    /// <summary>清理早期版本使用的 cmd 启动器。</summary>
    private static void CleanupLegacyLauncher()
    {
        try
        {
            if (File.Exists(AppPaths.LegacyLauncherCmd))
            {
                File.Delete(AppPaths.LegacyLauncherCmd);
            }
        }
        catch
        {
            // 残留文件不影响功能
        }
    }

    private static void ExtractResource(string resourceName, string targetPath)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"安装包缺少嵌入资源：{resourceName}");

        var tempPath = targetPath + ".tmp";
        using (var file = File.Create(tempPath))
        {
            stream.CopyTo(file);
        }

        File.Move(tempPath, targetPath, overwrite: true);
    }

    /// <summary>卸载：停止并删除任务、删除程序目录。运行数据默认保留（含设备身份）。</summary>
    public async Task<IReadOnlyList<InstallStep>> UninstallAsync(bool removeData, CancellationToken ct = default)
    {
        var steps = new List<InstallStep>();

        var stop = await _task.StopAsync(ct).ConfigureAwait(false);
        steps.Add(new InstallStep("停止 Agent", true, stop.Success ? "已停止" : "任务未运行"));

        var delete = await _task.DeleteAsync(ct).ConfigureAwait(false);
        steps.Add(new InstallStep("删除计划任务", delete.Success, delete.Success ? "已删除" : delete.CombinedOutput));

        try
        {
            if (File.Exists(AppPaths.AgentExe))
            {
                File.Delete(AppPaths.AgentExe);
            }

            // 安装目录下的控制软件副本可能正是当前进程，删不掉就留待重启后清理
            try
            {
                if (File.Exists(AppPaths.ControlExe) &&
                    !string.Equals(Environment.ProcessPath, AppPaths.ControlExe,
                        StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(AppPaths.ControlExe);
                }
            }
            catch
            {
                // 被占用时忽略
            }

            steps.Add(new InstallStep("删除程序文件", true, AppPaths.InstallRoot));
        }
        catch (Exception ex)
        {
            steps.Add(new InstallStep("删除程序文件", false, ex.Message));
        }

        if (removeData)
        {
            try
            {
                Directory.Delete(AppPaths.DataRoot, recursive: true);
                steps.Add(new InstallStep("删除运行数据", true, "已删除设备身份、配置与任务记录"));
            }
            catch (Exception ex)
            {
                steps.Add(new InstallStep("删除运行数据", false, ex.Message));
            }
        }
        else
        {
            steps.Add(new InstallStep("保留运行数据", true, $"设备身份与配置保留在 {AppPaths.DataRoot}"));
        }

        return steps;
    }
}
