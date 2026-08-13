using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZhanClawControl.Services;

/// <summary>
/// agent-config.json 的读写。
/// 使用 JsonNode 往返，未知字段原样保留——Agent 未来新增配置项时不会被本程序抹掉。
/// </summary>
public sealed class AgentConfigService
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public bool Exists => File.Exists(AppPaths.ConfigFile);

    public JsonObject Load()
    {
        if (!Exists)
        {
            return CreateDefault();
        }

        var text = File.ReadAllText(AppPaths.ConfigFile, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(text))
        {
            return CreateDefault();
        }

        return JsonNode.Parse(text) as JsonObject ?? CreateDefault();
    }

    public void Save(JsonObject config)
    {
        Directory.CreateDirectory(AppPaths.DataRoot);

        var json = config.ToJsonString(WriteOptions);

        // Agent 使用 Go 的 encoding/json 读取，UTF-8 无 BOM 最稳妥
        var tempPath = AppPaths.ConfigFile + ".tmp";
        File.WriteAllText(tempPath, json, new UTF8Encoding(false));
        File.Move(tempPath, AppPaths.ConfigFile, overwrite: true);
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
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        config[key] = array;
    }
}
