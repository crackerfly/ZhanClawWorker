namespace ZhanClawControl.Models;

public sealed class UiState
{
    /// <summary>PeerID -> user-defined local display name.</summary>
    public Dictionary<string, string> PeerNotes { get; set; } = new();

    /// <summary>Hide the main window to the notification area when it is closed.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Last whitelist snapshot created before emergency revocation.</summary>
    public List<string> LastAllowedPeersBackup { get; set; } = new();

    /// <summary>Auto, zh-CN, zh-TW, or en-US.</summary>
    public string Language { get; set; } = "Auto";

    /// <summary>Last whitelist proven to be loaded by a healthy Agent instance.</summary>
    public List<string> EffectiveAllowedPeers { get; set; } = new();

    /// <summary>False until a restart and local API health check have both succeeded.</summary>
    public bool EffectiveAllowedPeersKnown { get; set; }

    /// <summary>The config file changed after the last verified Agent start.</summary>
    public bool AuthorizationPendingRestart { get; set; }

    /// <summary>Non-authorization Agent settings changed after the last verified start.</summary>
    public bool ConfigurationPendingRestart { get; set; }
}
