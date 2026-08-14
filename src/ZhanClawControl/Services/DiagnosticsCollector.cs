#nullable disable warnings
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ZhanClawControl.Services;

public static class DiagnosticsCollector
{
	public static Task<string> CollectAsync(CancellationToken ct = default(CancellationToken))
	{
		return CollectAsync(includeBusinessOutput: false, ct);
	}

	public static async Task<string> CollectAsync(bool includeBusinessOutput, CancellationToken ct = default(CancellationToken))
	{
		ct.ThrowIfCancellationRequested();
		StringBuilder sb = new StringBuilder();
		Line("战 Claw 被控端 诊断信息");
		Line($"采集时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
		Line(includeBusinessOutput ? "输出模式：完整（可能包含设备名、PeerID、任务输出与文件路径）" : "输出模式：默认脱敏（不导出原始任务输出与 Agent 日志）");
		Section("环境");
		Line("控制软件版本：" + GetOwnVersion());
		Line("控制软件路径：" + (includeBusinessOutput ? Environment.ProcessPath : Path.GetFileName(Environment.ProcessPath)));
		Line($"操作系统：{Environment.OSVersion}");
		Line($"64 位进程：{Environment.Is64BitProcess}");
		Line("计算机名：" + (includeBusinessOutput ? Environment.MachineName : RedactIdentifier(Environment.MachineName)));
		Line("当前账户：" + (includeBusinessOutput ? InstallerService.CurrentUserName : RedactIdentifier(InstallerService.CurrentUserName)));
		Section("文件");
		(string, string)[] array = new(string, string)[8]
		{
			("Agent 主程序", AppPaths.AgentExe),
			("后台宿主", AppPaths.ControlExe),
			("配置文件", AppPaths.ConfigFile),
			("私网密钥", AppPaths.SwarmKeyFile),
			("设备身份", AppPaths.IdentityFile),
			("本机 API Token", AppPaths.ApiTokenFile),
			("任务记录", AppPaths.JournalFile),
			("运行日志", AppPaths.AgentLogFile)
		};
		for (int i = 0; i < array.Length; i++)
		{
			var (value, path) = array[i];
			Line($"{value,-16} {DescribeFile(path)}");
		}
		Section("Agent 版本");
		Line(ReadAgentStaticVersion());
		Section("进程");
		Line($"p2p-agent 运行中：{ScheduledTaskService.IsAgentProcessRunning()}");
		bool value2 = await ControlApiClient.IsPortOpenAsync(800, ct).ConfigureAwait(continueOnCapturedContext: false);
		Line($"回环端口 {"127.0.0.1"}:{7432} 可连接：{value2}");
		Section("计划任务");
		ScheduledTaskService taskService = new ScheduledTaskService();
		ScheduledTaskInspection taskInspection = await taskService.InspectAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		TaskState value3 = await taskService.GetStateAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		Line(taskInspection.QueryFailed ? ("查询失败：" + taskInspection.QueryError) : $"存在={taskInspection.Exists}  状态={value3}  定义匹配={taskInspection.MatchesExpectedDefinition}  问题数={taskInspection.Issues.Count}");
		Section("配置（allowed_peers 与关键项）");
		Line(DescribeConfig(includeBusinessOutput));
		Section(includeBusinessOutput ? "/v1/info 原始响应" : "/v1/info 脱敏摘要");
		using (ControlApiClient api = new ControlApiClient())
		{
			AgentInfo agentInfo = await api.GetInfoAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			if ((object)agentInfo == null)
			{
				Line("（无响应：端口未监听、token 不可读，HTTP 失败或响应无法解析）");
			}
			else if (includeBusinessOutput)
			{
				Line(agentInfo.RawJson);
			}
			else
			{
				Line($"peer_id={RedactIdentifier(agentInfo.PeerId)}  version={SafeInline(agentInfo.Version)}  relay_peer_id={RedactIdentifier(agentInfo.RelayPeerId)}  reservation_ready={FormatOptionalBool(agentInfo.ReservationReady)}  mdns_ready={FormatOptionalBool(agentInfo.MdnsReady)}  listen_address_count={agentInfo.ListenAddresses.Count}  running_tasks={agentInfo.RunningTasks?.ToString() ?? "未提供"}  available_slots={agentInfo.AvailableTaskSlots?.ToString() ?? "未提供"}");
			}
			Section(includeBusinessOutput ? "/v1/peers 原始响应" : "/v1/peers 原始响应（已省略）");
			if (includeBusinessOutput)
			{
				Line((await api.GetPeersRawAsync(ct).ConfigureAwait(continueOnCapturedContext: false)) ?? "（无响应）");
			}
			else
			{
				Line("（默认脱敏模式已省略；仅在明确选择完整诊断时导出）");
			}
			Section("/v1/peers 解析结果");
			PeerQueryResult peerQueryResult = await api.GetPeersResultAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!peerQueryResult.Success)
			{
				Line("（读取失败：" + SafeInline(peerQueryResult.ErrorCode) + "）");
			}
			else if (peerQueryResult.Peers.Count == 0)
			{
				Line("（无已连接 Peer）");
			}
			else
			{
				foreach (PeerEntry peer in peerQueryResult.Peers)
				{
					Line(includeBusinessOutput ? $"{peer.PeerId}  name={peer.Name}  path={peer.ConnectionPath}  scope={peer.Scope}" : $"{RedactIdentifier(peer.PeerId)}  name=（已省略）  path={RedactConnectionPath(peer.ConnectionPath)}  scope=（已省略）");
				}
			}
		}
		Section(includeBusinessOutput ? "任务记录尾部（最多 20 条原始行）" : "任务记录尾部（最多 20 条脱敏摘要）");
		JournalService journal = new JournalService();
		if (!journal.Exists)
		{
			Line("（journal 文件不存在；当前没有可供管理器读取的本地任务记录）");
		}
		else
		{
			IReadOnlyList<JournalRecord> readOnlyList = await journal.ReadRecentAsync(20, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (journal.LastReadError != null)
			{
				Line("（journal 读取失败：" + SafeInline(journal.LastReadError) + "）");
			}
			else if (readOnlyList.Count == 0)
			{
				Line("（journal 文件存在但为空）");
			}
			else
			{
				foreach (JournalRecord item in readOnlyList)
				{
					Line(includeBusinessOutput ? item.Detail : FormatJournalSummary(item));
				}
			}
		}
		Section("运行日志尾部（最多 80 行）");
		AgentLogService agentLogService = new AgentLogService();
		if (includeBusinessOutput)
		{
			Line(await agentLogService.ReadTailAsync(80, ct).ConfigureAwait(continueOnCapturedContext: false));
		}
		else
		{
			Line(agentLogService.Exists ? $"（默认脱敏模式已省略原始日志；当前文件 {agentLogService.SizeBytes:N0} 字节）" : "（日志文件不存在）");
		}
		Line();
		Line(includeBusinessOutput ? "=====（诊断信息结束。不导出密钥或 API Token 值；原始日志或任务输出仍可能含业务数据）=====" : "=====（诊断信息结束。已省略原始任务输出与 Agent 日志）=====");
		return sb.ToString();
		void Line(string text = "")
		{
			sb.AppendLine(text);
		}
		void Section(string title)
		{
			Line();
			Line("===== " + title + " =====");
		}
	}

	private static string FormatJournalSummary(JournalRecord record)
	{
		if (record.ParseSucceeded)
		{
			bool? acknowledged = record.Acknowledged;
			string text = ((!acknowledged.HasValue) ? "not_recorded" : ((acknowledged != true) ? "false" : "true"));
			string value = text;
			string value2 = $"  error_present={!string.IsNullOrWhiteSpace(record.Error)}";
			return $"time={record.Timestamp?.ToUniversalTime():O}  command={RedactIdentifier(record.CommandId)}  source={RedactIdentifier(record.SourcePeer)}  action={SafeInline(record.Action)}  state={SafeInline(record.State)}  status={SafeInline(record.Status)}  duration_ms={SafeInline(record.DurationMs)}  acknowledged={value}{value2}";
		}
		return $"parse_error={SafeInline(record.ParseError)}  raw_line_chars={record.Detail.Length}";
	}

	private static string RedactIdentifier(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "<empty>";
		}
		string text = value.Trim();
		string value2 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).Substring(0, 12).ToLowerInvariant();
		return $"<redacted:length={text.Length},sha256={value2}>";
	}

	private static string SafeInline(string? value, int maxLength = 240)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "";
		}
		string text = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ')
			.Trim();
		if (text.Length > maxLength)
		{
			return text.Substring(0, maxLength) + $"…（已截断，原长 {text.Length}）";
		}
		return text;
	}

	private static string RedactConnectionPath(string? value)
	{
		string text = SafeInline(value, 80);
		string text2 = text.ToUpperInvariant();
		string[] array = new string[5] { "SERVER_ROUTER", "DISCONNECTED", "DIRECT", "RELAY", "LOCAL" };
		foreach (string text3 in array)
		{
			if (HasConnectionPathPrefix(text2, text3))
			{
				return text3;
			}
		}
		if (text2.Length != 0)
		{
			return RedactIdentifier(text);
		}
		return "";
	}

	private static bool HasConnectionPathPrefix(string value, string category)
	{
		if (!value.StartsWith(category, StringComparison.Ordinal))
		{
			return false;
		}
		if (value.Length == category.Length)
		{
			return true;
		}
		switch (value[category.Length])
		{
		case ' ':
		case '(':
		case '-':
		case '/':
		case ':':
			return true;
		default:
			return false;
		}
	}

	private static string FormatOptionalBool(bool? value)
	{
		if (value.HasValue)
		{
			if (value == true)
			{
				return "true";
			}
			return "false";
		}
		return "未提供";
	}

	private static string DescribeFile(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return "不存在  " + path;
			}
			FileInfo fileInfo = new FileInfo(path);
			return $"{fileInfo.Length,12:N0} 字节  {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}  {path}";
		}
		catch (Exception ex)
		{
			return "读取失败（" + ex.GetType().Name + "）  " + path;
		}
	}

	private static string GetOwnVersion()
	{
		try
		{
			string processPath = Environment.ProcessPath;
			return (processPath == null) ? "未知" : (FileVersionInfo.GetVersionInfo(processPath).FileVersion ?? "未知");
		}
		catch
		{
			return "未知";
		}
	}

	private static string ReadAgentStaticVersion()
	{
		if (!File.Exists(AppPaths.AgentExe))
		{
			return "（p2p-agent.exe 不存在）";
		}
		try
		{
			FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(AppPaths.AgentExe);
			return string.IsNullOrWhiteSpace(versionInfo.FileVersion) ? "（PE 未提供 FileVersion；运行版本可查看鉴权 /v1/info）" : versionInfo.FileVersion;
		}
		catch (Exception ex)
		{
			return "（静态版本读取失败：" + ex.Message + "）";
		}
	}

	private static string DescribeConfig(bool includeBusinessOutput)
	{
		try
		{
			AgentConfigService agentConfigService = new AgentConfigService();
			if (!agentConfigService.Exists)
			{
				return "（agent-config.json 不存在）";
			}
			JsonObject config = agentConfigService.Load();
			StringBuilder stringBuilder = new StringBuilder();
			string text = AgentConfigService.GetString(config, "agent_name");
			List<string> stringArray = AgentConfigService.GetStringArray(config, "agent_tags");
			stringBuilder.AppendLine(includeBusinessOutput ? ("agent_name        = " + text) : ("agent_name        = " + RedactIdentifier(text)));
			stringBuilder.AppendLine(includeBusinessOutput ? ("agent_tags        = " + string.Join(", ", stringArray)) : $"agent_tags        = {stringArray.Count} 条（默认省略内容）");
			string text2 = AgentConfigService.GetString(config, "rendezvous_group");
			stringBuilder.AppendLine(includeBusinessOutput ? ("rendezvous_group  = " + text2) : ("rendezvous_group  = " + RedactIdentifier(text2)));
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder2);
			handler.AppendLiteral("api_listen        = ");
			handler.AppendFormatted(AgentConfigService.GetString(config, "api_listen"));
			stringBuilder3.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder2);
			handler.AppendLiteral("max_parallel_tasks= ");
			handler.AppendFormatted(AgentConfigService.GetInt(config, "max_parallel_tasks", -1));
			stringBuilder4.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder2);
			handler.AppendLiteral("max_transfer_bytes= ");
			handler.AppendFormatted(AgentConfigService.GetLong(config, "max_transfer_bytes", -1L));
			stringBuilder5.AppendLine(ref handler);
			List<string> stringArray2 = AgentConfigService.GetStringArray(config, "bootstrap_addrs");
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder6 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(22, 1, stringBuilder2);
			handler.AppendLiteral("bootstrap_addrs   = ");
			handler.AppendFormatted(stringArray2.Count);
			handler.AppendLiteral(" 条");
			stringBuilder6.AppendLine(ref handler);
			if (includeBusinessOutput)
			{
				foreach (string item in stringArray2)
				{
					stringBuilder2 = stringBuilder;
					StringBuilder stringBuilder7 = stringBuilder2;
					handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder2);
					handler.AppendLiteral("                    ");
					handler.AppendFormatted(item);
					stringBuilder7.AppendLine(ref handler);
				}
			}
			else if (stringArray2.Count > 0)
			{
				stringBuilder.AppendLine("                    （默认省略地址内容）");
			}
			List<string> stringArray3 = AgentConfigService.GetStringArray(config, "allowed_peers");
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder8 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(22, 1, stringBuilder2);
			handler.AppendLiteral("allowed_peers     = ");
			handler.AppendFormatted(stringArray3.Count);
			handler.AppendLiteral(" 条");
			stringBuilder8.AppendLine(ref handler);
			foreach (string item2 in stringArray3)
			{
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder9 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder2);
				handler.AppendLiteral("                    ");
				handler.AppendFormatted(includeBusinessOutput ? item2 : RedactIdentifier(item2));
				stringBuilder9.AppendLine(ref handler);
			}
			if (stringArray3.Count == 0)
			{
				stringBuilder.AppendLine("  提示：管理器未配置获准主控；远端请求的最终处理取决于已部署 Agent 的实际策略。");
			}
			return stringBuilder.ToString().TrimEnd();
		}
		catch (Exception ex)
		{
			return "（读取配置失败：" + ex.Message + "）";
		}
	}
}
