using System.IO;
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
    string Detail)
{
    public string DurationText => DurationMs.Length == 0 ? "" : $"{DurationMs} ms";

    public string ShortCommandId =>
        CommandId.Length > 12 ? CommandId[..12] : CommandId;

    public string ShortSourcePeer =>
        SourcePeer.Length > 14 ? $"{SourcePeer[..6]}…{SourcePeer[^6..]}" : SourcePeer;

    public string TimestampText =>
        Timestamp?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "";
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

                var parsed = ParseLine(line);
                if (parsed is not null)
                {
                    records.Add(parsed);
                }
            }
        }
        catch
        {
            // 文件被 Agent 独占写入时可能短暂失败，下次轮询即可
        }

        records.Reverse();
        return records.Take(maxRecords).ToList();
    }

    private static JournalRecord? ParseLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // 字段名取自 p2p-agent.exe 内嵌的 json 结构体标签，非猜测
            var commandId = Pick(root, "command_id", "message_id", "id") ?? "";
            var source = Pick(root, "origin", "from", "executed_by", "peer_id", "source") ?? "";
            var action = Pick(root, "action", "primitive", "operation") ?? "";
            var state = Pick(root, "state", "kind", "type", "mode") ?? "";
            var status = Pick(root, "status", "reason") ?? "";

            DateTime? ts = null;
            var tsText = Pick(root, "timestamp", "sent_at_utc", "captured_at_utc", "updated_utc", "modified_utc");
            if (tsText is not null && DateTime.TryParse(
                    tsText,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsedTs))
            {
                ts = parsedTs;
            }

            // 嵌套在 result 里的字段也纳入
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
            {
                if (status.Length == 0)
                {
                    status = Pick(result, "status", "reason") ?? "";
                }

                if (action.Length == 0)
                {
                    action = Pick(result, "action", "primitive", "operation") ?? "";
                }

                if (commandId.Length == 0)
                {
                    commandId = Pick(result, "command_id", "message_id") ?? "";
                }
            }

            var duration = Pick(root, "duration_ms");

            return new JournalRecord(ts, commandId, source, action, state, status, duration ?? "", line);
        }
        catch
        {
            return null;
        }
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

    /// <summary>
    /// 只读取文件尾部，避免 journal 长期增长后整文件加载。
    /// journal 是 append-only 且当前无自动压缩（见 ARCHITECTURE.md §24）。
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
