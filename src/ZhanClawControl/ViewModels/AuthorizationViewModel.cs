using System.Collections.ObjectModel;
using System.Windows;
using ZhanClawControl.Infrastructure;
using ZhanClawControl.Models;
using ZhanClawControl.Services;

namespace ZhanClawControl.ViewModels;

/// <summary>
/// allowed_peers 是被控端唯一的远端授权边界（ARCHITECTURE.md §19）。
/// 该配置是静态的，修改后必须重启 Agent 才生效——界面必须把这一点说清楚。
/// </summary>
public sealed class AuthorizationViewModel : ObservableObject
{
    private readonly AgentConfigService _config = new();
    private readonly ScheduledTaskService _task = new();
    private readonly UiStateService _uiStateService = new();
    private readonly ControlApiClient _api;

    private string _newPeerId = "";
    private string _newNote = "";
    private AllowedPeerItem? _selected;
    private bool _pendingRestart;
    private bool _isBusy;

    public AuthorizationViewModel(ControlApiClient api)
    {
        _api = api;

        AddCommand = new RelayCommand(Add, CanAdd);
        RemoveCommand = new RelayCommand(Remove, () => Selected is not null);
        PasteCommand = new RelayCommand(PasteFromClipboard);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        RevokeAllCommand = new AsyncRelayCommand(RevokeAllAsync, () => !IsBusy && Peers.Count > 0);
        RestoreBackupCommand = new AsyncRelayCommand(RestoreBackupAsync, () => !IsBusy && HasBackup);
    }

    public ObservableCollection<AllowedPeerItem> Peers { get; } = new();

    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand PasteCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand RevokeAllCommand { get; }
    public AsyncRelayCommand RestoreBackupCommand { get; }

    public string NewPeerId
    {
        get => _newPeerId;
        set
        {
            if (SetProperty(ref _newPeerId, value))
            {
                AddCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(NewPeerIdHint));
            }
        }
    }

    public string NewNote
    {
        get => _newNote;
        set => SetProperty(ref _newNote, value);
    }

    public string NewPeerIdHint
    {
        get
        {
            if (NewPeerId.Trim().Length == 0)
            {
                return "粘贴主控端安装时显示的完整 PeerID（12D3KooW… 开头）";
            }

            return AllowedPeerItem.LooksLikePeerId(NewPeerId)
                ? "格式看起来正常"
                : "格式可疑：PeerID 不含空格，长度通常为 52 个 base58 字符";
        }
    }

    public AllowedPeerItem? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                RemoveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool PendingRestart
    {
        get => _pendingRestart;
        private set => SetProperty(ref _pendingRestart, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
                RevokeAllCommand.RaiseCanExecuteChanged();
                RestoreBackupCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasBackup => _uiStateService.Load().LastAllowedPeersBackup.Count > 0;

    public bool IsEmpty => Peers.Count == 0;

    public event EventHandler? AuthorizationChanged;

    public void Load()
    {
        var config = _config.Load();
        var peerIds = AgentConfigService.GetStringArray(config, "allowed_peers");
        var notes = _uiStateService.Load().PeerNotes;

        Peers.Clear();
        foreach (var peerId in peerIds)
        {
            Peers.Add(new AllowedPeerItem
            {
                PeerId = peerId,
                Note = notes.TryGetValue(peerId, out var note) ? note : ""
            });
        }

        PendingRestart = false;
        RaiseCollectionDependents();
    }

    public async Task RefreshOnlineStateAsync(CancellationToken ct = default)
    {
        if (!ControlApiClient.IsPortOpen(300))
        {
            foreach (var peer in Peers)
            {
                peer.Online = false;
            }

            return;
        }

        var connected = await _api.GetPeersAsync(ct).ConfigureAwait(true);
        var set = connected.Select(p => p.PeerId).ToHashSet(StringComparer.Ordinal);

        foreach (var peer in Peers)
        {
            peer.Online = set.Contains(peer.PeerId);
        }
    }

    private bool CanAdd()
    {
        var value = NewPeerId.Trim();
        return value.Length > 0 && Peers.All(p => !string.Equals(p.PeerId, value, StringComparison.Ordinal));
    }

    private void Add()
    {
        var value = NewPeerId.Trim();

        if (value == "*")
        {
            MessageBox.Show(
                "通配符 \"*\" 会授权私有网络中的所有成员控制本机，仅适用于隔离测试环境，本程序不允许写入。\n\n" +
                "请填写具体的主控端 PeerID。",
                "不允许的授权",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!AllowedPeerItem.LooksLikePeerId(value))
        {
            var proceed = MessageBox.Show(
                "这段文本不像一个合法的 PeerID。\n\n仍然添加吗？（Agent 启动时会再次校验）",
                "格式确认",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);

            if (proceed != MessageBoxResult.OK)
            {
                return;
            }
        }

        Peers.Add(new AllowedPeerItem { PeerId = value, Note = NewNote.Trim() });
        NewPeerId = "";
        NewNote = "";
        PendingRestart = true;
        RaiseCollectionDependents();
    }

    private void Remove()
    {
        if (Selected is null)
        {
            return;
        }

        var target = Selected;
        var label = target.Note.Length > 0 ? $"{target.Note}（{target.ShortPeerId}）" : target.ShortPeerId;

        var confirm = MessageBox.Show(
            $"撤销对以下主控设备的授权？\n\n{label}\n\n撤销后该设备将无法向本机提交任何任务。",
            "撤销授权",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        Peers.Remove(target);
        Selected = null;
        PendingRestart = true;
        RaiseCollectionDependents();
    }

    private void PasteFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                NewPeerId = Clipboard.GetText().Trim();
            }
        }
        catch
        {
            // 剪贴板被其他进程占用
        }
    }

    private async Task SaveAsync()
    {
        IsBusy = true;

        try
        {
            PersistToConfig(Peers.Select(p => p.PeerId).ToList());
            PersistNotes();

            var restart = MessageBox.Show(
                "授权列表已保存。\n\nallowed_peers 是静态配置，需要重启 Agent 才会生效。\n\n现在重启？",
                "保存成功",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);

            if (restart == MessageBoxResult.OK)
            {
                await RestartAgentAsync().ConfigureAwait(true);
                PendingRestart = false;
            }

            AuthorizationChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败：{ex.Message}", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RevokeAllAsync()
    {
        var confirm = MessageBox.Show(
            "这会清空授权列表并立即重启 Agent，之后本机将拒绝所有远端任务。\n\n" +
            "设备身份、配置和任务记录都会保留，可随时恢复。\n\n" +
            "注意：已经在执行中的任务会被中断，且已产生的副作用不会回滚。\n\n继续？",
            "紧急断开全部授权",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var state = _uiStateService.Load();
            state.LastAllowedPeersBackup = Peers.Select(p => p.PeerId).ToList();
            _uiStateService.Save(state);

            PersistToConfig(new List<string>());
            Peers.Clear();
            RaiseCollectionDependents();

            await RestartAgentAsync().ConfigureAwait(true);
            PendingRestart = false;
            AuthorizationChanged?.Invoke(this, EventArgs.Empty);

            MessageBox.Show(
                "已断开全部授权，Agent 已重启。\n\n本机当前拒绝所有远端操作。",
                "已断开",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"操作失败：{ex.Message}", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasBackup));
            RestoreBackupCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task RestoreBackupAsync()
    {
        var state = _uiStateService.Load();
        if (state.LastAllowedPeersBackup.Count == 0)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"恢复上次断开前的 {state.LastAllowedPeersBackup.Count} 条授权，并重启 Agent？",
            "恢复授权",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var notes = state.PeerNotes;
            Peers.Clear();
            foreach (var peerId in state.LastAllowedPeersBackup)
            {
                Peers.Add(new AllowedPeerItem
                {
                    PeerId = peerId,
                    Note = notes.TryGetValue(peerId, out var note) ? note : ""
                });
            }

            PersistToConfig(state.LastAllowedPeersBackup);
            RaiseCollectionDependents();

            await RestartAgentAsync().ConfigureAwait(true);
            AuthorizationChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"恢复失败：{ex.Message}", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void PersistToConfig(IReadOnlyList<string> peerIds)
    {
        var config = _config.Load();
        AgentConfigService.SetStringArray(config, "allowed_peers", peerIds);
        _config.Save(config);
    }

    private void PersistNotes()
    {
        var state = _uiStateService.Load();
        foreach (var peer in Peers)
        {
            if (peer.Note.Trim().Length > 0)
            {
                state.PeerNotes[peer.PeerId] = peer.Note.Trim();
            }
            else
            {
                state.PeerNotes.Remove(peer.PeerId);
            }
        }

        _uiStateService.Save(state);
    }

    private async Task RestartAgentAsync()
    {
        await _task.StopAsync().ConfigureAwait(true);
        await Task.Delay(1500).ConfigureAwait(true);
        await _task.StartAsync().ConfigureAwait(true);
        await InstallerService.WaitForReadyAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(true);
    }

    private void RaiseCollectionDependents()
    {
        OnPropertyChanged(nameof(IsEmpty));
        RevokeAllCommand.RaiseCanExecuteChanged();
        RestoreBackupCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasBackup));
    }
}
