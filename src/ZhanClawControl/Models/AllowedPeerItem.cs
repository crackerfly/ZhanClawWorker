using ZhanClawControl.Infrastructure;
using ZhanClawControl.Services;

namespace ZhanClawControl.Models;

public sealed class AllowedPeerItem : ObservableObject
{
    private string _peerId = "";
    private string _note = "";
    private bool _online;

    public string PeerId
    {
        get => _peerId;
        set
        {
            if (SetProperty(ref _peerId, value))
            {
                OnPropertyChanged(nameof(ShortPeerId));
            }
        }
    }

    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
    }

    /// <summary>该主控当前是否在已连接 Peer 列表中。</summary>
    public bool Online
    {
        get => _online;
        set
        {
            if (SetProperty(ref _online, value))
            {
                OnPropertyChanged(nameof(OnlineText));
            }
        }
    }

    public string OnlineText => Online
        ? App.Localization.Text("AuthorizationOnline")
        : App.Localization.Text("AuthorizationOffline");

    public void RefreshLanguage() => OnPropertyChanged(nameof(OnlineText));

    public string ShortPeerId =>
        PeerId.Length > 20 ? $"{PeerId[..10]}…{PeerId[^8..]}" : PeerId;

    /// <summary>
    /// Delegates to the central base58/multihash validator so every UI entry point
    /// uses the same boundary as configuration persistence.
    /// </summary>
    public static bool LooksLikePeerId(string value)
    {
        return AgentConfigService.IsValidPeerId(value);
    }
}
