using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ZhanClawControl.Services;

public sealed record AgentInfo(
    string PeerId,
    string Version,
    string AgentName,
    string RelayPeerId,
    bool? ReservationReady,
    bool? MdnsReady,
    int? ConnectedRemoteCount,
    int? RunningTasks,
    int? AvailableTaskSlots,
    IReadOnlyList<string> ListenAddresses,
    string RawJson);

public sealed record PeerEntry(
    string PeerId,
    string Name,
    string ConnectionPath,
    string Scope)
{
    public string ShortPeerId =>
        PeerId.Length > 14 ? $"{PeerId[..6]}…{PeerId[^6..]}" : PeerId;
}

/// <summary>
/// 只访问回环 Control API 的两个只读端点。被控端不需要四个 Primitive。
///
/// 注意：/v1/info 与 /v1/peers 的确切 JSON 字段名未在 ARCHITECTURE.md 中给出，
/// 因此这里对多个候选字段名做容错探测，并保留原始 JSON 供诊断页显示。
/// 若后续 Agent 版本变更字段名，只需在 PickString 的候选列表里补一项。
/// </summary>
public sealed class ControlApiClient : IDisposable
{
    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private readonly HttpClient _http;

    public ControlApiClient()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(AppPaths.ApiBaseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    /// <summary>TCP 层探测，比 HTTP 请求快，用于频繁的存活轮询。</summary>
    public static bool IsPortOpen(int timeoutMs = 500)
    {
        try
        {
            using var client = new TcpClient();
            var async = client.BeginConnect(AppPaths.ApiHost, AppPaths.ApiPort, null, null);
            if (!async.AsyncWaitHandle.WaitOne(timeoutMs, false))
            {
                return false;
            }

            client.EndConnect(async);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadToken()
    {
        try
        {
            if (!File.Exists(AppPaths.ApiTokenFile))
            {
                return null;
            }

            var token = File.ReadAllText(AppPaths.ApiTokenFile, Encoding.UTF8).Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> GetAsync(string path, CancellationToken ct)
    {
        var token = ReadToken();
        if (token is null)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public async Task<AgentInfo?> GetInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await GetAsync("/v1/info", ct).ConfigureAwait(false);
            if (json is null)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var root = Unwrap(doc.RootElement);

            // 字段名取自 p2p-agent.exe 内嵌的 json 结构体标签，非猜测
            var peerId = PickString(root, "peer_id", "peerId", "id");
            var version = PickString(root, "version");
            var agentName = PickString(root, "agent_name", "name");
            var relayPeerId = PickString(root, "relay_peer_id");

            return new AgentInfo(
                peerId ?? "",
                version ?? "",
                agentName ?? "",
                relayPeerId ?? "",
                PickBool(root, "reservation_ready"),
                PickBool(root, "mdns_ready"),
                PickInt(root, "connected_remote_count"),
                PickInt(root, "running_tasks"),
                PickInt(root, "available_task_slots"),
                PickStringArray(root, "listen_addresses", "addresses", "addrs"),
                Prettify(json));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>取 /v1/peers 的原始响应，供诊断使用（解析失败时才能定位字段名）。</summary>
    public async Task<string?> GetPeersRawAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await GetAsync("/v1/peers", ct).ConfigureAwait(false);
            return json is null ? null : Prettify(json);
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<PeerEntry>> GetPeersAsync(CancellationToken ct = default)
    {
        var result = new List<PeerEntry>();

        try
        {
            var json = await GetAsync("/v1/peers", ct).ConfigureAwait(false);
            if (json is null)
            {
                return result;
            }

            using var doc = JsonDocument.Parse(json);
            var array = FindArray(doc.RootElement, "peers", "nodes", "providers");
            if (array is null)
            {
                return result;
            }

            foreach (var element in array.Value.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    result.Add(new PeerEntry(element.GetString() ?? "", "", "", ""));
                    continue;
                }

                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var peerId = PickString(element, "peer_id", "peerId", "id") ?? "";
                var name = PickString(element, "agent_name", "name") ?? "";
                var path = PickString(element, "connection_path", "delivery_path") ?? "";
                var scope = PickString(element, "scope", "role", "kind") ?? "";

                if (peerId.Length > 0)
                {
                    result.Add(new PeerEntry(peerId, name, path, scope));
                }
            }
        }
        catch
        {
            // 保持空列表
        }

        return result;
    }

    private static JsonElement Unwrap(JsonElement root)
    {
        // 兼容 { "result": {...} } 或 { "data": {...} } 包装
        foreach (var key in new[] { "result", "data", "info" })
        {
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(key, out var inner) &&
                inner.ValueKind == JsonValueKind.Object)
            {
                return inner;
            }
        }

        return root;
    }

    private static JsonElement? FindArray(JsonElement root, params string[] candidates)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in candidates)
        {
            if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                return value;
            }
        }

        foreach (var wrapper in new[] { "result", "data" })
        {
            if (root.TryGetProperty(wrapper, out var inner))
            {
                var nested = FindArray(inner, candidates);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string? PickString(JsonElement element, params string[] candidates)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in candidates)
        {
            if (!element.TryGetProperty(key, out var value))
            {
                continue;
            }

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
                    return value.ToString();
            }
        }

        return null;
    }

    private static bool? PickBool(JsonElement element, params string[] candidates)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in candidates)
        {
            if (element.TryGetProperty(key, out var value))
            {
                if (value.ValueKind == JsonValueKind.True) return true;
                if (value.ValueKind == JsonValueKind.False) return false;
            }
        }

        return null;
    }

    private static int? PickInt(JsonElement element, params string[] candidates)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in candidates)
        {
            if (element.TryGetProperty(key, out var value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var number))
            {
                return number;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> PickStringArray(JsonElement element, params string[] candidates)
    {
        var result = new List<string>();
        if (element.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var key in candidates)
        {
            if (!element.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        result.Add(text);
                    }
                }
            }

            if (result.Count > 0)
            {
                break;
            }
        }

        return result;
    }

    private static string Prettify(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, PrettyOptions);
        }
        catch
        {
            return json;
        }
    }

    public void Dispose() => _http.Dispose();
}
