using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace ZhanClawControl.Services;

/// <summary>
/// agent-config.json 的读写。
/// 使用 JsonNode 往返，未知字段原样保留——Agent 未来新增配置项时不会被本程序抹掉。
/// </summary>
public sealed class AgentConfigService
{
    // 必须显式指定 TypeInfoResolver：
    // JsonNode.ToJsonString 写出 JsonValue<T> 节点时要从 options 取 JsonTypeInfo<T>，
    // 而手工 new 出来的 JsonSerializerOptions 的 TypeInfoResolver 为 null，会抛
    // "JsonSerializerOptions instance must specify a TypeInfoResolver setting"。
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public bool Exists => File.Exists(AppPaths.ConfigFile);

    public JsonObject Load()
    {
        if (!Exists)
        {
            return CreateDefault();
        }

        RuntimeSecurityService.RejectReparsePoint(AppPaths.ConfigFile);
        var text = File.ReadAllText(AppPaths.ConfigFile, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException("agent-config.json 为空。");
        }

        return JsonNode.Parse(text) as JsonObject
               ?? throw new InvalidDataException("agent-config.json 根节点必须是 JSON 对象。");
    }

    public void Save(JsonObject config)
    {
        // Installer creates and hardens DataRoot. Creating it here would silently bypass that boundary.
        RuntimeSecurityService.ValidateSecureDataRootForWrite();

        // allowed_peers 是管理器写入的重要来源策略。无论调用方来自安装向导、设置页、
        // 备份恢复还是未来代码，都在唯一写入边界重新校验；最终请求策略由 Agent 实现决定。
        ValidateAndNormalizeAllowedPeers(config);

        var json = config.ToJsonString(WriteOptions);

        // Agent 使用 Go 的 encoding/json 读取，UTF-8 无 BOM 最稳妥
        var tempPath = Path.Combine(AppPaths.DataRoot, $".agent-config.{Guid.NewGuid():N}.tmp");
        try
        {
            // A random CreateNew file prevents collisions with attacker-precreated fixed .tmp names.
            // Parent and target are rechecked around the operation; pure path APIs cannot eliminate
            // every concurrent rename race, so failures remain fail-closed.
            using (var stream = new FileStream(
                       tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 16 * 1024, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            RuntimeSecurityService.ValidateSecureDataRootForWrite();
            RuntimeSecurityService.RejectReparsePoint(tempPath);
            if (File.Exists(AppPaths.ConfigFile)) RuntimeSecurityService.RejectReparsePoint(AppPaths.ConfigFile);
            File.Move(tempPath, AppPaths.ConfigFile, overwrite: true);
            RuntimeSecurityService.RejectReparsePoint(AppPaths.ConfigFile);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    public static JsonObject CreateDefault()
    {
        var bootstrap = new JsonArray();
        foreach (var addr in AppPaths.DefaultBootstrapAddrs)
        {
            bootstrap.Add(addr);
        }

        var tags = new JsonArray { "worker" };

        return new JsonObject
        {
            ["agent_name"] = Environment.MachineName + "-worker",
            ["agent_tags"] = tags,
            ["bootstrap_addrs"] = bootstrap,
            ["swarm_key"] = ToJsonPath(AppPaths.SwarmKeyFile),
            ["identity_file"] = ToJsonPath(AppPaths.IdentityFile),
            ["rendezvous_group"] = AppPaths.DefaultRendezvousGroup,
            ["api_listen"] = $"{AppPaths.ApiHost}:{AppPaths.ApiPort}",
            ["api_token_file"] = ToJsonPath(AppPaths.ApiTokenFile),
            ["command_journal_file"] = ToJsonPath(AppPaths.JournalFile),
            ["max_parallel_tasks"] = AppPaths.DefaultMaxParallelTasks,
            ["max_transfer_bytes"] = AppPaths.DefaultMaxTransferBytes,
            ["allowed_peers"] = new JsonArray()
        };
    }

    /// <summary>配置里统一使用正斜杠，与官方示例一致。</summary>
    public static string ToJsonPath(string windowsPath) => windowsPath.Replace('\\', '/');

    // ---- 强类型访问器 ----

    public static string GetString(JsonObject config, string key, string fallback = "")
    {
        // 配置可能被手工编辑成非字符串类型，这里不允许抛异常
        if (config[key] is JsonValue value && value.TryGetValue<string>(out var text) && text is not null)
        {
            return text;
        }

        return fallback;
    }

    public static int GetInt(JsonObject config, string key, int fallback)
    {
        try
        {
            return config[key]?.GetValue<int>() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public static long GetLong(JsonObject config, string key, long fallback)
    {
        try
        {
            return config[key]?.GetValue<long>() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public static List<string> GetStringArray(JsonObject config, string key)
    {
        var result = new List<string>();
        if (config[key] is not JsonArray array)
        {
            return result;
        }

        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text) &&
                !string.IsNullOrWhiteSpace(text))
            {
                result.Add(text.Trim());
            }
        }

        return result;
    }

    public static void SetStringArray(JsonObject config, string key, IEnumerable<string> values)
    {
        if (string.Equals(key, "allowed_peers", StringComparison.Ordinal))
        {
            SetAllowedPeers(config, values);
            return;
        }

        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        config[key] = array;
    }

    public static void SetAllowedPeers(JsonObject config, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(values);

        var normalized = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in values)
        {
            var value = raw?.Trim() ?? "";
            if (!IsValidPeerId(value))
            {
                throw new InvalidDataException(
                    value == "*"
                        ? "allowed_peers 禁止使用通配符 *。"
                        : $"allowed_peers 包含无效的 libp2p PeerID：{value}");
            }

            if (seen.Add(value))
            {
                normalized.Add(value);
            }
        }

        config["allowed_peers"] = normalized;
    }

    public static void ValidateAllowedPeers(JsonObject config) =>
        ValidateAndNormalizeAllowedPeers(config);

    /// <summary>Host-side authorization and local-secret boundary; UI validation is not trusted.</summary>
    public static void ValidateRuntimeBoundary(JsonObject config)
    {
        ValidateAndNormalizeAllowedPeers(config);
        if (!string.Equals(GetString(config, "api_listen"), $"{AppPaths.ApiHost}:{AppPaths.ApiPort}", StringComparison.Ordinal))
            throw new InvalidDataException("api_listen 必须精确绑定本机控制端点。");
        ExpectExactConfiguredPath(config, "swarm_key", AppPaths.SwarmKeyFile);
        ExpectExactConfiguredPath(config, "identity_file", AppPaths.IdentityFile);
        ExpectExactConfiguredPath(config, "api_token_file", AppPaths.ApiTokenFile);
        ExpectExactConfiguredPath(config, "command_journal_file", AppPaths.JournalFile);
    }

    private static void ExpectExactConfiguredPath(JsonObject config, string key, string expected)
    {
        var configured = GetString(config, key).Replace('/', Path.DirectorySeparatorChar);
        try
        {
            if (!Path.IsPathFullyQualified(configured) ||
                !string.Equals(Path.GetFullPath(configured), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{key} 必须精确指向受保护的数据目录。");
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"{key} 路径无效。", ex);
        }
    }

    /// <summary>
    /// 验证 base58btc 编码的 libp2p PeerID 是否是完整 multihash。该检查会拒绝空值、通配符、
    /// 非 base58 字符、截断/多余字节与不合理摘要长度；Agent 仍会做最终密码学解析。
    /// </summary>
    public static bool IsValidPeerId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "*" || value.Length is < 20 or > 128 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryDecodeBase58(value, out var bytes) || bytes.Length < 3)
        {
            return false;
        }

        var offset = 0;
        if (!TryReadUnsignedVarint(bytes, ref offset, out var hashCode) ||
            !TryReadUnsignedVarint(bytes, ref offset, out var digestLength) ||
            digestLength is 0 or > 128 ||
            digestLength != (ulong)(bytes.Length - offset))
        {
            return false;
        }

        // 当前 libp2p PeerID 常见 identity(0x00) 与 sha2-256(0x12)。其他已注册
        // multihash 仍允许，只要编码和摘要边界严格正确；未知算法的最终接受权留给 Agent。
        return hashCode <= uint.MaxValue &&
               (hashCode != 0x12 || digestLength == 32);
    }

    private static void ValidateAndNormalizeAllowedPeers(JsonObject config)
    {
        if (config["allowed_peers"] is null)
        {
            config["allowed_peers"] = new JsonArray();
            return;
        }

        if (config["allowed_peers"] is not JsonArray array)
        {
            throw new InvalidDataException("allowed_peers 必须是字符串数组。");
        }

        var values = new List<string>();
        foreach (var item in array)
        {
            if (item is not JsonValue jsonValue ||
                !jsonValue.TryGetValue<string>(out var text) ||
                text is null)
            {
                throw new InvalidDataException("allowed_peers 必须只包含 PeerID 字符串。");
            }

            values.Add(text);
        }

        SetAllowedPeers(config, values);
    }

    private static bool TryDecodeBase58(string value, out byte[] decoded)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var littleEndian = new List<byte> { 0 };

        foreach (var character in value)
        {
            var digit = alphabet.IndexOf(character);
            if (digit < 0)
            {
                decoded = Array.Empty<byte>();
                return false;
            }

            var carry = digit;
            for (var i = 0; i < littleEndian.Count; i++)
            {
                carry += littleEndian[i] * 58;
                littleEndian[i] = (byte)(carry & 0xff);
                carry >>= 8;
            }

            while (carry > 0)
            {
                littleEndian.Add((byte)(carry & 0xff));
                carry >>= 8;
            }
        }

        var leadingZeroes = value.TakeWhile(c => c == '1').Count();
        var high = littleEndian.Count - 1;
        while (high > 0 && littleEndian[high] == 0)
        {
            high--;
        }

        decoded = new byte[leadingZeroes + high + 1];
        for (var i = 0; i <= high; i++)
        {
            decoded[decoded.Length - 1 - i] = littleEndian[i];
        }

        return true;
    }

    private static bool TryReadUnsignedVarint(byte[] bytes, ref int offset, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && offset < bytes.Length; shift += 7)
        {
            var current = bytes[offset++];
            if (shift == 63 && current > 1)
            {
                return false;
            }

            value |= (ulong)(current & 0x7f) << shift;
            if ((current & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }
}
