using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ZhanClawControl.Services;

public sealed class UiState
{
    /// <summary>PeerID -> 用户自定义备注名。仅本地展示用，不写入 agent-config.json。</summary>
    public Dictionary<string, string> PeerNotes { get; set; } = new();

    /// <summary>关闭主窗口时最小化到托盘而非退出。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>紧急断开授权时备份的白名单，用于一键恢复。</summary>
    public List<string> LastAllowedPeersBackup { get; set; } = new();
}

public sealed class UiStateService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public UiState Load()
    {
        try
        {
            if (!File.Exists(AppPaths.UiStateFile))
            {
                return new UiState();
            }

            var json = File.ReadAllText(AppPaths.UiStateFile, Encoding.UTF8);
            return JsonSerializer.Deserialize<UiState>(json) ?? new UiState();
        }
        catch
        {
            return new UiState();
        }
    }

    public void Save(UiState state)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            var json = JsonSerializer.Serialize(state, Options);
            File.WriteAllText(AppPaths.UiStateFile, json, new UTF8Encoding(false));
        }
        catch
        {
            // 状态文件写失败不影响主流程
        }
    }
}
