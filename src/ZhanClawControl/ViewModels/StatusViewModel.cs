using System.Collections.ObjectModel;
using System.Windows;
using ZhanClawControl.Infrastructure;
using ZhanClawControl.Services;

namespace ZhanClawControl.ViewModels;

public sealed class StatusViewModel : ObservableObject
{
    private readonly ControlApiClient _api;
    private readonly ScheduledTaskService _task = new();
    private readonly HashSet<string> _configuredPeerIds = new(StringComparer.Ordinal);
    private bool _agentRunning;
    private string _peerId = "";
    private string _agentVersion = "";
    private string _agentName = "";
    private string _taskStateText = "";
    private string _statusHeadline = "";
    private string _statusDetail = "";
    private string _busyMessage = "";
    private bool _isBusy;
    private bool _configurationPending;
    private bool _effectiveKnown;
    private string _connectivitySummary = "—";
    private string _deploymentIssues = "";

    public StatusViewModel(ControlApiClient api)
    {
        _api = api;
        _taskStateText = L("StatusTaskUnknown");
        _statusHeadline = L("StatusChecking");
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy && !AgentRunning);
        StopCommand = new AsyncRelayCommand(StopAsync, () => !IsBusy && AgentRunning);
        RestartCommand = new AsyncRelayCommand(RestartAsync, () => !IsBusy);
        CopyPeerIdCommand = new RelayCommand(CopyPeerId, () => PeerId.Length > 0);
        RepairCommand = new AsyncRelayCommand(RepairAsync, () => !IsBusy);
    }

    private static string L(string key) => App.Localization.Text(key);
    private static string F(string key, params object?[] values) => App.Localization.Format(key, values);

    public ObservableCollection<PeerEntry> ConnectedPeers { get; } = new();
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand RestartCommand { get; }
    public RelayCommand CopyPeerIdCommand { get; }
    public AsyncRelayCommand RepairCommand { get; }
    public event EventHandler? RuntimeRestartVerified;

    public string DeploymentIssues
    {
        get => _deploymentIssues;
        private set { if (SetProperty(ref _deploymentIssues, value)) OnPropertyChanged(nameof(ShowDeploymentWarning)); }
    }
    public bool ShowDeploymentWarning => DeploymentIssues.Length > 0;

    public bool AgentRunning
    {
        get => _agentRunning;
        private set
        {
            if (!SetProperty(ref _agentRunning, value)) return;
            OnPropertyChanged(nameof(RunningText));
            OnPropertyChanged(nameof(EffectiveAuthorizationSummary));
            StartCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }
    public string RunningText => AgentRunning ? L("StatusRunning") : L("StatusStopped");

    public string PeerId
    {
        get => _peerId;
        private set { if (SetProperty(ref _peerId, value)) CopyPeerIdCommand.RaiseCanExecuteChanged(); }
    }
    public string AgentVersion { get => _agentVersion; private set => SetProperty(ref _agentVersion, value); }
    public string AgentName { get => _agentName; private set => SetProperty(ref _agentName, value); }
    public string ConnectivitySummary { get => _connectivitySummary; private set => SetProperty(ref _connectivitySummary, value); }
    public string TaskStateText { get => _taskStateText; private set => SetProperty(ref _taskStateText, value); }
    public string StatusHeadline { get => _statusHeadline; private set => SetProperty(ref _statusHeadline, value); }
    public string StatusDetail { get => _statusDetail; private set => SetProperty(ref _statusDetail, value); }

    public int AuthorizedCount => _configuredPeerIds.Count;
    public bool ShowNoAuthorizationWarning => _configuredPeerIds.Count == 0;
    public string AuthorizationSummary => ConfiguredAuthorizationSummary;
    public string ConfiguredAuthorizationSummary =>
        (_configuredPeerIds.Count == 0 ? L("StatusConfiguredNone") : F("StatusConfiguredCount", _configuredPeerIds.Count)) +
        (_configurationPending ? L("StatusAuthorizationPending") : "");
    public string EffectiveAuthorizationSummary => !AgentRunning || !_effectiveKnown
        ? L("StatusEffectiveUnknown")
        : AuthorizedPeerIds.Count == 0
            ? L("StatusEffectiveNone")
            : F("StatusEffectiveCount", AuthorizedPeerIds.Count);
    public HashSet<string> AuthorizedPeerIds { get; } = new(StringComparer.Ordinal);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            StartCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
            RestartCommand.RaiseCanExecuteChanged();
            RepairCommand.RaiseCanExecuteChanged();
        }
    }
    public string BusyMessage { get => _busyMessage; private set => SetProperty(ref _busyMessage, value); }

    public void SetConfiguredAuthorization(IEnumerable<string> peerIds, bool pendingRestart)
    {
        var next = peerIds.ToHashSet(StringComparer.Ordinal);
        if (!_configuredPeerIds.SetEquals(next))
        {
            _configuredPeerIds.Clear();
            _configuredPeerIds.UnionWith(next);
            OnPropertyChanged(nameof(AuthorizedCount));
            OnPropertyChanged(nameof(ShowNoAuthorizationWarning));
        }
        if (_configurationPending != pendingRestart) _configurationPending = pendingRestart;
        OnPropertyChanged(nameof(AuthorizationSummary));
        OnPropertyChanged(nameof(ConfiguredAuthorizationSummary));
    }

    public void MarkAuthorizationEffective(IEnumerable<string> peerIds)
    {
        AuthorizedPeerIds.Clear();
        AuthorizedPeerIds.UnionWith(peerIds);
        _effectiveKnown = true;
        _configurationPending = false;
        OnPropertyChanged(nameof(EffectiveAuthorizationSummary));
        OnPropertyChanged(nameof(ConfiguredAuthorizationSummary));
        OnPropertyChanged(nameof(AuthorizationSummary));
    }

    public void InitializeEffectiveAuthorization(IEnumerable<string> peerIds, bool known)
    {
        AuthorizedPeerIds.Clear();
        AuthorizedPeerIds.UnionWith(peerIds);
        _effectiveKnown = known;
        OnPropertyChanged(nameof(EffectiveAuthorizationSummary));
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var portOpen = await ControlApiClient.IsPortOpenAsync(400, ct).ConfigureAwait(true);
        var processAlive = ScheduledTaskService.IsAgentProcessRunning();
        AgentRunning = portOpen && processAlive;

        var state = await _task.GetStateAsync(ct).ConfigureAwait(true);
        TaskStateText = state switch
        {
            TaskState.NotInstalled => L("StatusTaskMissing"),
            TaskState.Ready => L("StatusTaskReady"),
            TaskState.Running => L("StatusTaskRunning"),
            TaskState.Disabled => L("StatusTaskDisabled"),
            _ => L("StatusTaskUnknown")
        };

        if (AgentRunning)
        {
            var info = await _api.GetInfoAsync(ct).ConfigureAwait(true);
            if (info is not null)
            {
                PeerId = info.PeerId;
                AgentVersion = info.Version;
                AgentName = info.AgentName;
                StatusHeadline = L("StatusProcessAndApiReady");
                StatusDetail = _configuredPeerIds.Count == 0
                    ? L("StatusProcessAndApiReadyNoAuth")
                    : L("StatusProcessAndApiReadyAuth");
            }
            else
            {
                StatusHeadline = L("StatusApiUnavailable");
                StatusDetail = L("StatusApiUnavailableDetail");
            }

            var peers = await _api.GetPeersAsync(ct).ConfigureAwait(true);
            SyncPeers(peers);
            ConnectivitySummary = BuildConnectivitySummary(peers);
        }
        else
        {
            PeerId = "";
            AgentVersion = "";
            AgentName = "";
            ConnectivitySummary = "—";
            ConnectedPeers.Clear();
            OnPropertyChanged(nameof(EffectiveAuthorizationSummary));
            StatusHeadline = processAlive ? L("StatusProcessNoPort") : L("StatusNotRunning");
            StatusDetail = processAlive ? L("StatusProcessNoPortDetail") : L("StatusNotRunningDetail");
        }
    }

    private string BuildConnectivitySummary(IReadOnlyList<PeerEntry> peers)
    {
        if (peers.Count == 0) return L("StatusNoConnections");
        var summary = F("StatusConnectionsCount", peers.Count);
        if (_effectiveKnown && AuthorizedPeerIds.Count > 0)
        {
            var authorizedOnline = peers.Count(p => AuthorizedPeerIds.Contains(p.PeerId));
            summary += authorizedOnline > 0
                ? F("StatusAuthorizedConnectionsCount", authorizedOnline)
                : L("StatusNoAuthorizedConnections");
        }
        var paths = peers.Select(p => p.ConnectionPath).Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count > 0) summary += F("StatusConnectionPaths", string.Join(" · ", paths));
        return summary;
    }

    private void SyncPeers(IReadOnlyList<PeerEntry> peers)
    {
        ConnectedPeers.Clear();
        foreach (var peer in peers) ConnectedPeers.Add(peer);
    }

    private async Task StartAsync()
    {
        IsBusy = true;
        BusyMessage = L("StatusStarting");
        try
        {
            // A listening/API failure does not prove that an older host or Agent
            // instance is absent. Stop the exact installed product processes
            // first so a later healthy API can only certify a newly submitted run.
            var stop = await _task.StopAsync().ConfigureAwait(true);
            if (!stop.Success)
            {
                ShowError(F("DialogStartFailed", stop.CombinedOutput));
                return;
            }
            var result = await _task.StartAsync().ConfigureAwait(true);
            if (!result.Success)
            {
                ShowError(F("DialogStartFailed", result.CombinedOutput));
                return;
            }
            if (!await InstallerService.WaitForReadyAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(true))
            {
                ShowWarning(L("DialogStartTimeout"));
                return;
            }
            RuntimeRestartVerified?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { ShowError(F("DialogStartFailed", ex.Message)); }
        finally
        {
            IsBusy = false;
            BusyMessage = "";
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    private async Task StopAsync()
    {
        if (MessageBox.Show(L("DialogStopConfirm"), L("CommonStop"), MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        IsBusy = true;
        BusyMessage = L("StatusStopping");
        try
        {
            var result = await _task.StopAsync().ConfigureAwait(true);
            if (!result.Success) ShowError(F("DialogStopFailed", result.CombinedOutput));
        }
        catch (Exception ex) { ShowError(F("DialogStopFailed", ex.Message)); }
        finally
        {
            IsBusy = false;
            BusyMessage = "";
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    private async Task RestartAsync()
    {
        IsBusy = true;
        BusyMessage = L("StatusRestarting");
        try
        {
            var stop = await _task.StopAsync().ConfigureAwait(true);
            if (!stop.Success) { ShowError(F("DialogRestartError", stop.CombinedOutput)); return; }
            var start = await _task.StartAsync().ConfigureAwait(true);
            if (!start.Success) { ShowError(F("DialogRestartError", start.CombinedOutput)); return; }
            if (!await InstallerService.WaitForReadyAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(true))
            {
                ShowError(F("DialogRestartError", L("DialogStartTimeout")));
                return;
            }
            RuntimeRestartVerified?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { ShowError(F("DialogRestartError", ex.Message)); }
        finally
        {
            IsBusy = false;
            BusyMessage = "";
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    private void CopyPeerId()
    {
        try { Clipboard.SetText(PeerId); }
        catch (Exception ex) { ShowError(F("DialogCopyFailed", ex.Message)); }
    }

    private async Task RepairAsync()
    {
        if (MessageBox.Show(L("DialogRepairConfirm"), L("StatusRepair"), MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK) return;
        IsBusy = true;
        BusyMessage = L("StatusRepairing");
        try
        {
            var steps = await new InstallerService().RepairAsync().ConfigureAwait(true);
            var failed = steps.Where(step => !step.Success).ToList();
            if (failed.Count > 0)
                ShowWarning(F("DialogOperationFailed", string.Join(Environment.NewLine,
                    failed.Select(step =>
                    {
                        var display = InstallStepPresenter.Present(step);
                        return $"· {display.Title}: {display.Detail}";
                    }))));
            else
                RuntimeRestartVerified?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { ShowError(F("DialogOperationFailed", ex.Message)); }
        finally
        {
            IsBusy = false;
            BusyMessage = "";
            await CheckDeploymentAsync().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    public async Task CheckDeploymentAsync(CancellationToken ct = default)
    {
        try
        {
            var issues = await InstallerService.CheckDeploymentAsync(ct).ConfigureAwait(true);
            DeploymentIssues = issues.Count == 0
                ? ""
                : string.Join(Environment.NewLine, issues.Select(issue =>
                    "· " + (issue.Detail.Length == 0
                        ? L(issue.ResourceKey)
                        : F(issue.ResourceKey, issue.Detail))));
        }
        catch (Exception ex) { DeploymentIssues = F("StatusDeploymentCheckFailed", ex.Message); }
    }

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(RunningText));
        OnPropertyChanged(nameof(AuthorizationSummary));
        OnPropertyChanged(nameof(ConfiguredAuthorizationSummary));
        OnPropertyChanged(nameof(EffectiveAuthorizationSummary));
    }

    private static void ShowError(string text) => MessageBox.Show(text, L("ProductName"), MessageBoxButton.OK, MessageBoxImage.Error);
    private static void ShowWarning(string text) => MessageBox.Show(text, L("ProductName"), MessageBoxButton.OK, MessageBoxImage.Warning);
}
