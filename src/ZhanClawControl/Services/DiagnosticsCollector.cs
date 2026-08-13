using System.Diagnostics;
using System.IO;
using System.Text;

namespace ZhanClawControl.Services;

/// <summary>
/// 一键收集排障所需的全部信息。
/// 目的是把「出问题 → 来回追问」压缩成「复制一段文本」。
/// 严格排除机密：swarm.key、agent-identity.key、agent-api.token 只报告是否存在与大小，绝不读取内容。
/// </summary>
public static class DiagnosticsCollector
{
    public static async Task<string> CollectAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        void Line(string text = "") => sb.AppendLine(text);
        void Section(string title)
        {
            Line();
            Line($"===== {title} =====");
        }

        Line($"{AppInfo.ProductName} 诊断信息");
        Line($"采集时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");

        Section("环境");
        Line($"控制软件版本：{GetOwnVersion()}");
        Line($"控制软件路径：{Environment.ProcessPath}");
        Line($"操作系统：{Environment.OSVersion}");
        Line($"64 位进程：{Environment.Is64BitProcess}");
        Line($"计算机名：{Environment.MachineName}");
        Line($"当前账户：{InstallerService.CurrentUserName}");

        Section("文件");
        foreach (var (label, path) in new[]
                 {
                     ("Agent 主程序", AppPaths.AgentExe),
                     ("后台宿主", AppPaths.ControlExe),
                     ("配置文件", AppPaths.ConfigFile),
                     ("私网密钥", AppPaths.SwarmKeyFile),
                     ("设备身份", AppPaths.IdentityFile),
                     ("本机 API Token", AppPaths.ApiTokenFile),
                     ("任务记录", AppPaths.JournalFile),
                     ("运行日志", AppPaths.AgentLogFile)
                 })
        {
            Line($"{label,-16} {DescribeFile(path)}");
        }

        Section("Agent 版本");
        Line(await RunAgentVersionAsync(ct).ConfigureAwait(false));

        Section("进程");
        Line($"p2p-agent 运行中：{ScheduledTaskService.IsAgentProcessRunning()}");
        Line($"回环端口 {AppPaths.ApiHost}:{AppPaths.ApiPort} 可连接：{ControlApiClient.IsPortOpen(800)}");

        Section("计划任务");
        var task = await ProcessRunner.RunAsync(
            ProcessRunner.SystemPath("schtasks.exe"),
            new[] { "/Query", "/TN", AppPaths.ScheduledTaskName, "/FO", "LIST", "/V" },
            20_000,
            ct).ConfigureAwait(false);
        Line(task.Success ? task.StdOut.Trim() : $"查询失败：{task.CombinedOutput}");

        Section("配置（allowed_peers 与关键项）");
        Line(DescribeConfig());

        Section("/v1/info 原始响应");
        using (var api = new ControlApiClient())
        {
            var info = await api.GetInfoAsync(ct).ConfigureAwait(false);
            Line(info is null ? "（无响应：端口未监听、token 不可读，或请求失败）" : info.RawJson);

            Section("/v1/peers 解析结果");
            var peers = await api.GetPeersAsync(ct).ConfigureAwait(false);
            if (peers.Count == 0)
            {
                Line("（无已连接 Peer）");
            }
            else
            {
                foreach (var peer in peers)
                {
                    Line($"{peer.PeerId}  name={peer.Name}  path={peer.ConnectionPath}  scope={peer.Scope}");
                }
            }
        }

        Section("任务记录尾部（最多 20 条原始行）");
        var journal = new JournalService();
        if (!journal.Exists)
        {
            Line("（journal 文件不存在，说明本机尚未收到过任何远端 Command）");
        }
        else
        {
            var records = await journal.ReadRecentAsync(20, ct).ConfigureAwait(false);
            if (records.Count == 0)
            {
                Line("（journal 文件存在但为空）");
            }
            else
            {
                foreach (var record in records)
                {
                    Line(record.Detail);
                }
            }
        }

        Section("运行日志尾部（最多 80 行）");
        var log = new AgentLogService();
        Line(await log.ReadTailAsync(80, ct).ConfigureAwait(false));

        Line();
        Line("=====（诊断信息结束。本文件不包含 swarm.key、设备私钥或 API Token 的内容）=====");

        return sb.ToString();
    }

    private static string DescribeFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return $"不存在  {path}";
            }

            var info = new FileInfo(path);
            return $"{info.Length,12:N0} 字节  {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}  {path}";
        }
        catch (Exception ex)
        {
            return $"读取失败（{ex.GetType().Name}）  {path}";
        }
    }

    private static string GetOwnVersion()
    {
        try
        {
            var path = Environment.ProcessPath;
            return path is null
                ? "未知"
                : FileVersionInfo.GetVersionInfo(path).FileVersion ?? "未知";
        }
        catch
        {
            return "未知";
        }
    }

    /// <summary>Agent 支持 -version（官方安装脚本用它做版本一致性校验）。</summary>
    private static async Task<string> RunAgentVersionAsync(CancellationToken ct)
    {
        if (!File.Exists(AppPaths.AgentExe))
        {
            return "（p2p-agent.exe 不存在）";
        }

        try
        {
            var result = await ProcessRunner
                .RunAsync(AppPaths.AgentExe, new[] { "-version" }, 15_000, ct)
                .ConfigureAwait(false);

            return result.CombinedOutput.Length > 0
                ? result.CombinedOutput
                : $"（无输出，退出码 {result.ExitCode}）";
        }
        catch (Exception ex)
        {
            return $"（调用失败：{ex.Message}）";
        }
    }

    private static string DescribeConfig()
    {
        try
        {
            var service = new AgentConfigService();
            if (!service.Exists)
            {
                return "（agent-config.json 不存在）";
            }

            var config = service.Load();
            var sb = new StringBuilder();

            sb.AppendLine($"agent_name        = {AgentConfigService.GetString(config, "agent_name")}");
            sb.AppendLine($"agent_tags        = {string.Join(", ", AgentConfigService.GetStringArray(config, "agent_tags"))}");
            sb.AppendLine($"rendezvous_group  = {AgentConfigService.GetString(config, "rendezvous_group")}");
            sb.AppendLine($"api_listen        = {AgentConfigService.GetString(config, "api_listen")}");
            sb.AppendLine($"max_parallel_tasks= {AgentConfigService.GetInt(config, "max_parallel_tasks", -1)}");
            sb.AppendLine($"max_transfer_bytes= {AgentConfigService.GetLong(config, "max_transfer_bytes", -1)}");

            var bootstrap = AgentConfigService.GetStringArray(config, "bootstrap_addrs");
            sb.AppendLine($"bootstrap_addrs   = {bootstrap.Count} 条");
            foreach (var addr in bootstrap)
            {
                sb.AppendLine($"                    {addr}");
            }

            var allowed = AgentConfigService.GetStringArray(config, "allowed_peers");
            sb.AppendLine($"allowed_peers     = {allowed.Count} 条");
            foreach (var peer in allowed)
            {
                sb.AppendLine($"                    {peer}");
            }

            if (allowed.Count == 0)
            {
                sb.AppendLine("  ⚠ 白名单为空：本机会拒绝所有远端任务，这是主控端任务不执行的最常见原因。");
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"（读取配置失败：{ex.Message}）";
        }
    }
}
