using System.Collections.ObjectModel;
using System.Windows;
using ZhanClawControl.Infrastructure;
using ZhanClawControl.Services;

namespace ZhanClawControl.ViewModels;

public sealed class StatusViewModel : ObservableObject
{
    private readonly ControlApiClient _api;
    private readonly ScheduledTaskService _task = new();
    private readonly AgentLogService _log = new();

    private bool _agentRunning;
    private string _peerId = "";
    private string _agentVersion = "";
    private string _agentName = "";
    private string _taskStateText = "未知";
    private string _statusHeadline = "正在检测…";
    private string _statusDetail = "";
    private string _busyMessage = "";
    private bool _isBusy;
    private int _authorizedCount;

    public StatusViewModel(ControlApiClient api)
    {
        _api = api;

        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy && !AgentRunning);
        StopCommand = new AsyncRelayCommand(StopAsync, () => !IsBusy && AgentRunning);
        RestartCommand = new AsyncRelayCommand(RestartAsync, () => !IsBusy);
        CopyPeerIdCommand = new RelayCommand(CopyPeerId, () => PeerId.Length > 0);
    }

    public ObservableCollection<PeerEntry> ConnectedPeers { get; } = new();

    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand RestartCommand { get; }
    public RelayCommand CopyPeerIdCommand { get; }

    public bool AgentRunning
    {
        get => _agentRunning;
        private set
        {
            if (SetProperty(ref _agentRunning, value))
            {
                OnPropertyChanged(nameof(RunningText));
                StartCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string RunningText => AgentRunning ? "运行中" : "已停止";

    public string PeerId
    {
        get => _peerId;
        private set
        {
            if (SetProperty(ref _peerId, value))
            {
                CopyPeerIdCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string AgentVersion
    {
        get => _agentVersion;
        private set => SetProperty(ref _agentVersion, value);
    }

    public string AgentName
    {
        get => _agentName;
        private set => SetProperty(ref _agentName, value);
    }

    public string TaskStateText
    {
        get => _taskStateText;
        private set => SetProperty(ref _taskStateText, value);
    }

    public string StatusHeadline
    {
        get => _statusHeadline;
        private set => SetProperty(ref _statusHeadline, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    public int AuthorizedCount
    {
        get => _authorizedCount;
        set
        {
            if (SetProperty(ref _authorizedCount, value))
            {
                OnPropertyChanged(nameof(AuthorizationSummary));
                OnPropertyChanged(nameof(ShowNoAuthorizationWarning));
            }
        }
    }

    public string AuthorizationSummary => AuthorizedCount == 0
        ? "未授权任何主控设备"
        : $"已授权 {AuthorizedCount} 台主控设备";

    /// <summary>白名单为空是安全的默认状态，不是故障；提示语必须写清楚。</summary>
    public bool ShowNoAuthorizationWarning => AuthorizedCount == 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                StartCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
                RestartCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string BusyMessage
    {
        get => _busyMessage;
        private set => SetProperty(ref _busyMessage, value);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var portOpen = ControlApiClient.IsPortOpen(400);
        var processAlive = ScheduledTaskService.IsAgentProcessRunning();

        AgentRunning = portOpen && processAlive;

        var state = await _task.GetStateAsync(ct).ConfigureAwait(true);
        TaskStateText = state switch
        {
            TaskState.NotInstalled => "未注册",
            TaskState.Ready => "已注册（就绪）",
            TaskState.Running => "已注册（运行中）",
            TaskState.Disabled => "已注册（已禁用）",
            _ => "已注册（状态未知）"
        };

        if (AgentRunning)
        {
            var info = await _api.GetInfoAsync(ct).ConfigureAwait(true);
            if (info is not null)
            {
                PeerId = info.PeerId;
                AgentVersion = info.Version;
                StatusHeadline = "本机已接入 P2P 网络";
                StatusDetail = "Agent 正在运行，可接受已授权主控设备的任务。";
            }
            else
            {
                StatusHeadline = "Agent 正在运行，但本机 API 无响应";
                StatusDetail = "端口已监听但读取 /v1/info 失败，请检查 agent-api.token 是否可读。";
            }

            var peers = await _api.GetPeersAsync(ct).ConfigureAwait(true);
            SyncPeers(peers);
        }
        else
        {
            PeerId = "";
            AgentVersion = "";
            ConnectedPeers.Clear();

            StatusHeadline = processAlive
                ? "Agent 进程存在但未监听本机端口"
                : "Agent 未运行";
            StatusDetail = processAlive
                ? "进程可能正在启动，或配置中的 api_listen 不是 127.0.0.1:7432。"
                : "本机当前不接受任何远端任务。点击「启动」恢复后台。";
        }

        _log.RollIfNeeded();
    }

    private void SyncPeers(IReadOnlyList<PeerEntry> peers)
    {
        ConnectedPeers.Clear();
        foreach (var peer in peers)
        {
            ConnectedPeers.Add(peer);
        }
    }

    private async Task StartAsync()
    {
        IsBusy = true;
        BusyMessage = "正在启动 Agent…";

        try
        {
            var result = await _task.StartAsync().ConfigureAwait(true);
            if (!result.Success)
            {
                MessageBox.Show(
                    $"启动计划任务失败：\n{result.CombinedOutput}",
                    AppInfo.ProductName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            var ready = await InstallerService.WaitForReadyAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(true);
            if (!ready)
            {
                MessageBox.Show(
                    "Agent 在 45 秒内没有监听 127.0.0.1:7432。请到「日志」页查看 agent.log。",
                    AppInfo.ProductName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            IsBusy = false;
            BusyMessage = "";
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task StopAsync()
    {
        var confirm = MessageBox.Show(
            "停止后本机将从 Fleet 中消失，正在执行的远端任务会被中断。\n\n" +
            "注意：已经产生副作用的 PowerShell 不会回滚。\n\n确定停止？",
            "停止 Agent",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        IsBusy = true;
        BusyMessage = "正在停止 Agent…";

        try
        {
            await _task.StopAsync().ConfigureAwait(true);
            await Task.Delay(1000).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = "";
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RestartAsync()
    {
        IsBusy = true;
        BusyMessage = "正在重启 Agent…";

        try
        {
            await _task.StopAsync().ConfigureAwait(true);
            await Task.Delay(1500).ConfigureAwait(true);
            await _task.StartAsync().ConfigureAwait(true);
            await InstallerService.WaitForReadyAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = "";
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private void CopyPeerId()
    {
        try
        {
            Clipboard.SetText(PeerId);
            MessageBox.Show(
                "本机 PeerID 已复制。\n\n把它发给主控端管理员，或在主控端的授权列表中添加。",
                "已复制",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"复制失败：{ex.Message}", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
