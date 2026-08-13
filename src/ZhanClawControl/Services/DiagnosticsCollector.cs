using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ZhanClawControl.Services;

/// <summary>
/// 一键收集排障所需的全部信息。
/// 目的是把「出问题 → 来回追问」压缩成「复制一段文本」。
/// 密钥文件只报告是否存在与大小，绝不主动读取。默认模式还会省略原始任务输出与 Agent 日志；
/// 完整模式可能包含业务程序自行打印到日志中的内容，导出前必须由用户明确选择。
/// </summary>
public static class DiagnosticsCollector
{
    /// <summary>
    /// 默认诊断不会导出原始 journal、Agent 日志、设备名等可能包含业务数据的内容。
    /// 现有调用签名保持兼容；需要完整原始输出时必须显式调用带 bool 参数的重载。
    /// </summary>
    public static Task<string> CollectAsync(CancellationToken ct = default) =>
        CollectAsync(includeBusinessOutput: false, ct: ct);

    public static async Task<string> CollectAsync(
        bool includeBusinessOutput,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sb = new StringBuilder();

        void Line(string text = "") => sb.AppendLine(text);
        void Section(string title)
        {
            Line();
            Line($"===== {title} =====");
        }

        Line($"{AppInfo.ProductName} 诊断信息");
        Line($"采集时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Line(includeBusinessOutput
            ? "输出模式：完整（可能包含设备名、PeerID、任务输出与文件路径）"
            : "输出模式：默认脱敏（不导出原始任务输出与 Agent 日志）");

        Section("环境");
        Line($"控制软件版本：{GetOwnVersion()}");
        Line($"控制软件路径：{(includeBusinessOutput ? Environment.ProcessPath : Path.GetFileName(Environment.ProcessPath))}");
        Line($"操作系统：{Environment.OSVersion}");
        Line($"64 位进程：{Environment.Is64BitProcess}");
        Line($"计算机名：{(includeBusinessOutput ? Environment.MachineName : RedactIdentifier(Environment.MachineName))}");
        Line($"当前账户：{(includeBusinessOutput ? InstallerService.CurrentUserName : RedactIdentifier(InstallerService.CurrentUserName))}");

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
        Line(ReadAgentStaticVersion());

        Section("进程");
        Line($"p2p-agent 运行中：{ScheduledTaskService.IsAgentProcessRunning()}");
        var portOpen = await ControlApiClient.IsPortOpenAsync(800, ct).ConfigureAwait(false);
        Line($"回环端口 {AppPaths.ApiHost}:{AppPaths.ApiPort} 可连接：{portOpen}");

        Section("计划任务");
        var taskService = new ScheduledTaskService();
        var taskInspection = await taskService.InspectAsync(ct).ConfigureAwait(false);
        var taskState = await taskService.GetStateAsync(ct).ConfigureAwait(false);
        Line(taskInspection.QueryFailed
            ? $"查询失败：{taskInspection.QueryError}"
            : $"存在={taskInspection.Exists}  状态={taskState}  定义匹配={taskInspection.MatchesExpectedDefinition}  " +
              $"问题数={taskInspection.Issues.Count}");

        Section("配置（allowed_peers 与关键项）");
        Line(DescribeConfig(includeBusinessOutput));

        Section(includeBusinessOutput ? "/v1/info 原始响应" : "/v1/info 脱敏摘要");
        using (var api = new ControlApiClient())
        {
            var info = await api.GetInfoAsync(ct).ConfigureAwait(false);
            if (info is null)
            {
                Line("（无响应：端口未监听、token 不可读，HTTP 失败或响应无法解析）");
            }
            else if (includeBusinessOutput)
            {
                Line(info.RawJson);
            }
            else
            {
                Line($"peer_id={RedactIdentifier(info.PeerId)}  version={SafeInline(info.Version)}  " +
                     $"running_tasks={info.RunningTasks?.ToString() ?? "未提供"}  " +
                     $"available_slots={info.AvailableTaskSlots?.ToString() ?? "未提供"}");
            }

            Section(includeBusinessOutput ? "/v1/peers 原始响应" : "/v1/peers 原始响应（已省略）");
            if (includeBusinessOutput)
            {
                var peersRaw = await api.GetPeersRawAsync(ct).ConfigureAwait(false);
                Line(peersRaw ?? "（无响应）");
            }
            else
            {
                Line("（默认脱敏模式已省略；仅在明确选择完整诊断时导出）");
            }

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
                    Line(includeBusinessOutput
                        ? $"{peer.PeerId}  name={peer.Name}  path={peer.ConnectionPath}  scope={peer.Scope}"
                        : $"{RedactIdentifier(peer.PeerId)}  name=（已省略）  " +
                          $"path={RedactConnectionPath(peer.ConnectionPath)}  scope=（已省略）");
                }
            }
        }

        Section(includeBusinessOutput
            ? "任务记录尾部（最多 20 条原始行）"
            : "任务记录尾部（最多 20 条脱敏摘要）");
        var journal = new JournalService();
        if (!journal.Exists)
        {
            Line("（journal 文件不存在；当前没有可供管理器读取的本地任务记录）");
        }
        else
        {
            var records = await journal.ReadRecentAsync(20, ct).ConfigureAwait(false);
            if (journal.LastReadError is not null)
            {
                Line($"（journal 读取失败：{SafeInline(journal.LastReadError)}）");
            }
            else if (records.Count == 0)
            {
                Line("（journal 文件存在但为空）");
            }
            else
            {
                foreach (var record in records)
                {
                    Line(includeBusinessOutput ? record.Detail : FormatJournalSummary(record));
                }
            }
        }

        Section("运行日志尾部（最多 80 行）");
        var log = new AgentLogService();
        if (includeBusinessOutput)
        {
            Line(await log.ReadTailAsync(80, ct).ConfigureAwait(false));
        }
        else
        {
            Line(log.Exists
                ? $"（默认脱敏模式已省略原始日志；当前文件 {log.SizeBytes:N0} 字节）"
                : "（日志文件不存在）");
        }

        Line();
        Line(includeBusinessOutput
            ? "=====（诊断信息结束。不主动读取密钥/Token，但原始日志或任务输出仍可能含业务数据）====="
            : "=====（诊断信息结束。已省略原始任务输出与 Agent 日志）=====");

        return sb.ToString();
    }

    private static string FormatJournalSummary(JournalRecord record)
    {
        if (!record.ParseSucceeded)
        {
            return $"parse_error={SafeInline(record.ParseError)}  raw_line_chars={record.Detail.Length}";
        }

        var acknowledged = record.Acknowledged switch
        {
            true => "true",
            false => "false",
            null => "not_recorded"
        };

        // Error 常含命令、路径或账户名；默认诊断只报告是否存在，不导出正文。
        var error = $"  error_present={!string.IsNullOrWhiteSpace(record.Error)}";

        return $"time={record.Timestamp?.ToUniversalTime():O}  " +
               $"command={RedactIdentifier(record.CommandId)}  source={RedactIdentifier(record.SourcePeer)}  " +
               $"action={SafeInline(record.Action)}  state={SafeInline(record.State)}  " +
               $"status={SafeInline(record.Status)}  duration_ms={SafeInline(record.DurationMs)}  " +
               $"acknowledged={acknowledged}{error}";
    }

    private static string RedactIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<empty>";
        }

        var trimmed = value.Trim();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed)))[..12]
            .ToLowerInvariant();
        return $"<redacted:length={trimmed.Length},sha256={digest}>";
    }

    private static string SafeInline(string? value, int maxLength = 240)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var inline = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();

        return inline.Length <= maxLength
            ? inline
            : inline[..maxLength] + $"…（已截断，原长 {inline.Length}）";
    }

    private static string RedactConnectionPath(string? value)
    {
        var path = SafeInline(value, 80);
        return path.ToUpperInvariant() switch
        {
            "DIRECT" or "RELAY" or "SERVER_ROUTER" => path.ToUpperInvariant(),
            "" => "",
            _ => RedactIdentifier(path)
        };
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

    /// <summary>读取 PE 静态版本元数据；诊断绝不以提权 GUI 令牌执行 Agent。</summary>
    private static string ReadAgentStaticVersion()
    {
        if (!File.Exists(AppPaths.AgentExe))
        {
            return "（p2p-agent.exe 不存在）";
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(AppPaths.AgentExe);
            return string.IsNullOrWhiteSpace(info.FileVersion)
                ? "（PE 未提供 FileVersion；运行版本可查看鉴权 /v1/info）"
                : info.FileVersion;
        }
        catch (Exception ex)
        {
            return $"（静态版本读取失败：{ex.Message}）";
        }
    }

    private static string DescribeConfig(bool includeBusinessOutput)
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

            var agentName = AgentConfigService.GetString(config, "agent_name");
            var agentTags = AgentConfigService.GetStringArray(config, "agent_tags");
            sb.AppendLine(includeBusinessOutput
                ? $"agent_name        = {agentName}"
                : $"agent_name        = {RedactIdentifier(agentName)}");
            sb.AppendLine(includeBusinessOutput
                ? $"agent_tags        = {string.Join(", ", agentTags)}"
                : $"agent_tags        = {agentTags.Count} 条（默认省略内容）");
            var rendezvous = AgentConfigService.GetString(config, "rendezvous_group");
            sb.AppendLine(includeBusinessOutput
                ? $"rendezvous_group  = {rendezvous}"
                : $"rendezvous_group  = {RedactIdentifier(rendezvous)}");
            sb.AppendLine($"api_listen        = {AgentConfigService.GetString(config, "api_listen")}");
            sb.AppendLine($"max_parallel_tasks= {AgentConfigService.GetInt(config, "max_parallel_tasks", -1)}");
            sb.AppendLine($"max_transfer_bytes= {AgentConfigService.GetLong(config, "max_transfer_bytes", -1)}");

            var bootstrap = AgentConfigService.GetStringArray(config, "bootstrap_addrs");
            sb.AppendLine($"bootstrap_addrs   = {bootstrap.Count} 条");
            if (includeBusinessOutput)
            {
                foreach (var addr in bootstrap)
                {
                    sb.AppendLine($"                    {addr}");
                }
            }
            else if (bootstrap.Count > 0)
            {
                sb.AppendLine("                    （默认省略地址内容）");
            }

            var allowed = AgentConfigService.GetStringArray(config, "allowed_peers");
            sb.AppendLine($"allowed_peers     = {allowed.Count} 条");
            foreach (var peer in allowed)
            {
                sb.AppendLine($"                    {(includeBusinessOutput ? peer : RedactIdentifier(peer))}");
            }

            if (allowed.Count == 0)
            {
                sb.AppendLine("  提示：管理器未配置获准主控；远端请求的最终处理取决于已部署 Agent 的实际策略。");
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"（读取配置失败：{ex.Message}）";
        }
    }
}
