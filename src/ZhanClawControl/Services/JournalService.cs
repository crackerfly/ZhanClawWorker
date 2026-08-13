using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ZhanClawControl.Services;

public sealed record JournalRecord(
    DateTime? Timestamp,
    string CommandId,
    string SourcePeer,
    string Action,
    string State,
    string Status,
    string DurationMs,
    string Error,
    string Detail)
{
    /// <summary>
    /// 本地 journal 捕获到的结果确认快照。null 表示该版本记录中没有此字段。
    /// 它不等同于命令执行状态；具体 ACK 时序仍由 p2p-agent 协议定义。
    /// </summary>
    public bool? Acknowledged { get; init; }

    /// <summary>该行是否成功解析为 journal 对象。失败行仍保留在记录列表中。</summary>
    public bool ParseSucceeded { get; init; } = true;

    /// <summary>解析失败原因；原始行始终保存在 Detail。</summary>
    public string ParseError { get; init; } = "";

    public string AcknowledgedText => Acknowledged switch
    {
        true => "true",
        false => "false",
        null => "—"
    };

    public string DurationText
    {
        get
        {
            if (DurationMs.Length == 0 ||
                !double.TryParse(DurationMs,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var ms))
            {
                return "";
            }

            return ms >= 1000
                ? (ms / 1000).ToString("0.0", CultureInfo.CurrentCulture) + " s"
                : ms.ToString("0", CultureInfo.CurrentCulture) + " ms";
        }
    }

    public string ShortCommandId =>
        CommandId.Length > 12 ? CommandId[..12] : CommandId;

    public string ShortSourcePeer =>
        SourcePeer.Length > 14 ? $"{SourcePeer[..6]}…{SourcePeer[^6..]}" : SourcePeer;

    public string TimestampText =>
        Timestamp?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";
}

/// <summary>
/// 读取 agent-command-journal.jsonl。
///
/// 这一屏是被控端 GUI 存在的主要理由：process_execute 拥有 Agent 账户的完整 PowerShell 权限，
/// 本机的使用者有权知道谁在什么时候让这台机器执行了什么。
///
/// journal 的确切字段名未在文档中给出，因此使用容错解析；无法识别的字段整行原样保留在 Detail 中。
/// </summary>
public sealed class JournalService
{
    public bool Exists => File.Exists(AppPaths.JournalFile);

    /// <summary>最近一次读取的文件级错误；解析错误会作为 JournalRecord 保留，不写入这里。</summary>
    public string? LastReadError { get; private set; }

    public long FileSizeBytes
    {
        get
        {
            try
            {
                return Exists ? new FileInfo(AppPaths.JournalFile).Length : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>读取最后 maxRecords 条记录，按时间倒序返回。</summary>
    public async Task<IReadOnlyList<JournalRecord>> ReadRecentAsync(
        int maxRecords = 300,
        CancellationToken ct = default)
    {
        var records = new List<JournalRecord>();
        LastReadError = null;
        if (!Exists)
        {
            return records;
        }

        try
        {
            var lines = await ReadLastLinesAsync(AppPaths.JournalFile, maxRecords * 2, ct).ConfigureAwait(false);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                records.Add(ParseLine(line));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastReadError = ex.GetType().Name;
        }

        records.Reverse();
        return records.Take(maxRecords).ToList();
    }

    private static JournalRecord ParseLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return CreateParseErrorRecord(
                    line,
                    $"journal_root_kind:{root.ValueKind}");
            }

            // 实测的 journal 记录形状（来自真机诊断）：
            // {"origin":..,"command_id":..,"fingerprint":..,"state":"accepted|completed",
            //  "result":{"status":"ok|error","duration_ms":N,"error":"..","output":{..}},
            //  "updated_utc":".."}
            // 根层没有 action 字段，动作类型只能从 result.output 的形状推断。
            var commandId = Pick(root, "command_id", "message_id", "id") ?? "";
            var source = Pick(root, "origin", "from", "peer_id", "source") ?? "";
            var action = Pick(root, "action", "primitive", "operation") ?? "";
            var state = Pick(root, "state", "kind", "type", "mode") ?? "";
            var status = Pick(root, "status", "reason") ?? "";
            var duration = Pick(root, "duration_ms") ?? "";
            var error = Pick(root, "error") ?? "";
            var acknowledged = PickBool(root, "acknowledged", "acked", "acknowledged_by_origin");

            DateTime? ts = null;
            var tsText = Pick(root, "updated_utc", "timestamp", "sent_at_utc", "captured_at_utc", "modified_utc");
            if (tsText is not null && DateTime.TryParse(
                    tsText,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsedTs))
            {
                ts = parsedTs;
            }

            // status / duration_ms / error 实际位于 result 内，不在根层
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
            {
                if (status.Length == 0)
                {
                    status = Pick(result, "status", "reason") ?? "";
                }

                if (duration.Length == 0)
                {
                    duration = Pick(result, "duration_ms") ?? "";
                }

                if (error.Length == 0)
                {
                    error = Pick(result, "error") ?? "";
                }

                if (commandId.Length == 0)
                {
                    commandId = Pick(result, "command_id", "message_id") ?? "";
                }

                if (action.Length == 0)
                {
                    action = InferAction(result);
                }

                acknowledged ??= PickBool(
                    result,
                    "acknowledged",
                    "acked",
                    "acknowledged_by_origin");

                if (acknowledged is null &&
                    result.TryGetProperty("output", out var output) &&
                    output.ValueKind == JsonValueKind.Object)
                {
                    acknowledged = PickBool(
                        output,
                        "acknowledged",
                        "acked",
                        "acknowledged_by_origin");
                }
            }

            return new JournalRecord(ts, commandId, source, action, state, status, duration, error, line)
            {
                Acknowledged = acknowledged
            };
        }
        catch (Exception ex)
        {
            return CreateParseErrorRecord(line, ex.GetType().Name);
        }
    }

    private static JournalRecord CreateParseErrorRecord(string line, string parseError) =>
        new(
            null,
            "",
            "",
            "",
            "parse_error",
            "unparsed",
            "",
            parseError,
            line)
        {
            ParseSucceeded = false,
            ParseError = parseError
        };

    /// <summary>
    /// journal 不记录动作类型，只能从 result.output 的字段形状反推。
    /// 依据来自各 Primitive 的实际返回结构。
    /// </summary>
    private static string InferAction(JsonElement result)
    {
        if (!result.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        if (output.TryGetProperty("collector", out var collector) &&
            collector.ValueKind == JsonValueKind.Object &&
            (collector.TryGetProperty("exit_code", out _) ||
             collector.TryGetProperty("shell", out _) ||
             collector.TryGetProperty("timed_out", out _)))
        {
            return "process_execute";
        }

        if (output.TryGetProperty("agent", out var agent) &&
            agent.ValueKind == JsonValueKind.Object &&
            agent.TryGetProperty("providers", out _))
        {
            return "fleet_inspect";
        }

        if (output.TryGetProperty("source_uri", out _) || output.TryGetProperty("destination_uri", out _))
        {
            return "resource_transfer";
        }

        if (output.TryGetProperty("entries", out _) ||
            output.TryGetProperty("resource_uri", out _) ||
            output.TryGetProperty("logical_disks", out _))
        {
            return "resource_inspect";
        }

        return "";
    }

    private static string? Pick(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value))
            {
                switch (value.ValueKind)
                {
                    case JsonValueKind.String:
                        var s = value.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            return s;
                        }

                        break;
                    case JsonValueKind.Number:
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        return value.ToString();
                }
            }
        }

        return null;
    }

    private static bool? PickBool(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            if (value.ValueKind == JsonValueKind.String &&
                bool.TryParse(value.GetString(), out var textValue))
            {
                return textValue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                if (number == 1) return true;
                if (number == 0) return false;
            }
        }

        return null;
    }

    /// <summary>
    /// 只读取文件尾部，避免 journal 长期增长后整文件加载。
    /// 当前观察到的 journal 是追加式文件；读取器仅扫描受限尾部，避免文件增长后整份载入。
    /// </summary>
    internal static async Task<List<string>> ReadLastLinesAsync(
        string path,
        int lineCount,
        CancellationToken ct,
        int maxTailBytes = 4 * 1024 * 1024)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var length = stream.Length;
        var take = (int)Math.Min(maxTailBytes, length);
        var start = length - take;

        stream.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[take];
        var offset = 0;
        while (offset < take)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, take - offset), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            offset += read;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, offset);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        // 从文件中部开始读时，第一行可能是残缺的 JSON，直接丢弃
        if (start > 0 && lines.Count > 0)
        {
            lines.RemoveAt(0);
        }

        return lines.Where(l => l.Length > 0).TakeLast(lineCount).ToList();
    }
}
