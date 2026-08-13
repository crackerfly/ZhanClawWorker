using ZhanClawControl.Infrastructure;

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

    public string OnlineText => Online ? "已连接" : "未连接";

    public string ShortPeerId =>
        PeerId.Length > 20 ? $"{PeerId[..10]}…{PeerId[^8..]}" : PeerId;

    /// <summary>
    /// libp2p PeerID 的基本格式校验。
    /// 与官方安装脚本使用同一判据：^[1-9A-HJ-NP-Za-km-z]{20,}$（base58btc 字符集，长度不设上限）。
    /// 这里只拦明显错误（粘贴了整行日志、带空格、含非法字符），真正的合法性由 Agent 判定。
    /// </summary>
    public static bool LooksLikePeerId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length < 20)
        {
            return false;
        }

        const string Base58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        return trimmed.All(c => Base58.Contains(c));
    }
}
