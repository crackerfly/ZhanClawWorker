#nullable disable warnings
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ZhanClawControl.Infrastructure;
using ZhanClawControl.Services;
using ZhanClawControl.Views.Dialogs;

namespace ZhanClawControl.ViewModels;

public sealed class StatusViewModel : ObservableObject
{
	private readonly ControlApiClient _api;

	private readonly ScheduledTaskService _task = new ScheduledTaskService();

	private readonly HashSet<string> _configuredPeerIds = new HashSet<string>(StringComparer.Ordinal);

	private bool _agentRunning;

	private bool _apiHealthy;

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

	private bool? _relayReservationReady;

	private bool? _mdnsReady;

	private int? _runningTasks;

	private int? _availableTaskSlots;

	private int _listenAddressCount;

	public ObservableCollection<PeerEntry> ConnectedPeers { get; } = new ObservableCollection<PeerEntry>();

	public AsyncRelayCommand StartCommand { get; }

	public AsyncRelayCommand StopCommand { get; }

	public AsyncRelayCommand RestartCommand { get; }

	public RelayCommand CopyPeerIdCommand { get; }

	public AsyncRelayCommand RepairCommand { get; }

	public string DeploymentIssues
	{
		get
		{
			return _deploymentIssues;
		}
		private set
		{
			if (SetProperty(ref _deploymentIssues, value, "DeploymentIssues"))
			{
				OnPropertyChanged("ShowDeploymentWarning");
			}
		}
	}

	public bool ShowDeploymentWarning => DeploymentIssues.Length > 0;

	public bool AgentRunning
	{
		get
		{
			return _agentRunning;
		}
		private set
		{
			if (SetProperty(ref _agentRunning, value, "AgentRunning"))
			{
				OnPropertyChanged("RunningText");
				OnPropertyChanged("EffectiveAuthorizationSummary");
				StartCommand.RaiseCanExecuteChanged();
				StopCommand.RaiseCanExecuteChanged();
			}
		}
	}

	public bool ApiHealthy
	{
		get
		{
			return _apiHealthy;
		}
		private set
		{
			if (SetProperty(ref _apiHealthy, value, "ApiHealthy"))
			{
				OnPropertyChanged("RunningText");
				OnPropertyChanged("EffectiveAuthorizationSummary");
				OnPropertyChanged("RelayReservationText");
				OnPropertyChanged("MdnsText");
				OnPropertyChanged("TaskCapacityText");
				OnPropertyChanged("ListenAddressesText");
			}
		}
	}

	public string RunningText
	{
		get
		{
			if (AgentRunning)
			{
				if (!ApiHealthy)
				{
					return L("StatusDegraded");
				}
				return L("StatusRunning");
			}
			return L("StatusStopped");
		}
	}

	public string PeerId
	{
		get
		{
			return _peerId;
		}
		private set
		{
			if (SetProperty(ref _peerId, value, "PeerId"))
			{
				CopyPeerIdCommand.RaiseCanExecuteChanged();
			}
		}
	}

	public string AgentVersion
	{
		get
		{
			return _agentVersion;
		}
		private set
		{
			SetProperty(ref _agentVersion, value, "AgentVersion");
		}
	}

	public string AgentName
	{
		get
		{
			return _agentName;
		}
		private set
		{
			SetProperty(ref _agentName, value, "AgentName");
		}
	}

	public string ConnectivitySummary
	{
		get
		{
			return _connectivitySummary;
		}
		private set
		{
			SetProperty(ref _connectivitySummary, value, "ConnectivitySummary");
		}
	}

	public string RelayReservationText
	{
		get
		{
			if (!ApiHealthy)
			{
				return "—";
			}
			bool? relayReservationReady = _relayReservationReady;
			return (!relayReservationReady.HasValue) ? L("CommonUnknown") : ((relayReservationReady != true) ? L("StatusRelayUnavailable") : L("StatusRelayReady"));
		}
	}

	public string MdnsText
	{
		get
		{
			if (!ApiHealthy)
			{
				return "—";
			}
			bool? mdnsReady = _mdnsReady;
			return (!mdnsReady.HasValue) ? L("CommonUnknown") : ((mdnsReady != true) ? L("StatusMdnsUnavailable") : L("StatusMdnsReady"));
		}
	}

	public string TaskCapacityText
	{
		get
		{
			if (ApiHealthy)
			{
				int? runningTasks = _runningTasks;
				if (!runningTasks.HasValue)
				{
					runningTasks = _availableTaskSlots;
					if (!runningTasks.HasValue)
					{
						goto IL_0092;
					}
				}
				return F("StatusTaskCapacity", _runningTasks?.ToString() ?? "—", _availableTaskSlots?.ToString() ?? "—");
			}
			goto IL_0092;
			IL_0092:
			return "—";
		}
	}

	public string ListenAddressesText
	{
		get
		{
			if (ApiHealthy)
			{
				return F("StatusListenAddressCount", _listenAddressCount);
			}
			return "—";
		}
	}

	public string TaskStateText
	{
		get
		{
			return _taskStateText;
		}
		private set
		{
			SetProperty(ref _taskStateText, value, "TaskStateText");
		}
	}

	public string StatusHeadline
	{
		get
		{
			return _statusHeadline;
		}
		private set
		{
			SetProperty(ref _statusHeadline, value, "StatusHeadline");
		}
	}

	public string StatusDetail
	{
		get
		{
			return _statusDetail;
		}
		private set
		{
			SetProperty(ref _statusDetail, value, "StatusDetail");
		}
	}

	public int AuthorizedCount => _configuredPeerIds.Count;

	public bool ShowNoAuthorizationWarning => _configuredPeerIds.Count == 0;

	public string AuthorizationSummary => ConfiguredAuthorizationSummary;

	public string ConfiguredAuthorizationSummary => ((_configuredPeerIds.Count == 0) ? L("StatusConfiguredNone") : F("StatusConfiguredCount", _configuredPeerIds.Count)) + (_configurationPending ? L("StatusAuthorizationPending") : "");

	public string EffectiveAuthorizationSummary
	{
		get
		{
			if (ApiHealthy && _effectiveKnown)
			{
				if (AuthorizedPeerIds.Count != 0)
				{
					return F("StatusEffectiveCount", AuthorizedPeerIds.Count);
				}
				return L("StatusEffectiveNone");
			}
			return L("StatusEffectiveUnknown");
		}
	}

	public HashSet<string> AuthorizedPeerIds { get; } = new HashSet<string>(StringComparer.Ordinal);

	public bool IsBusy
	{
		get
		{
			return _isBusy;
		}
		private set
		{
			if (SetProperty(ref _isBusy, value, "IsBusy"))
			{
				StartCommand.RaiseCanExecuteChanged();
				StopCommand.RaiseCanExecuteChanged();
				RestartCommand.RaiseCanExecuteChanged();
				RepairCommand.RaiseCanExecuteChanged();
			}
		}
	}

	public string BusyMessage
	{
		get
		{
			return _busyMessage;
		}
		private set
		{
			SetProperty(ref _busyMessage, value, "BusyMessage");
		}
	}

	public event EventHandler? RuntimeRestartVerified;

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

	private static string L(string key)
	{
		return App.Localization.Text(key);
	}

	private static string F(string key, params object?[] values)
	{
		return App.Localization.Format(key, values);
	}

	public void SetConfiguredAuthorization(IEnumerable<string> peerIds, bool pendingRestart)
	{
		HashSet<string> hashSet = peerIds.ToHashSet<string>(StringComparer.Ordinal);
		if (!_configuredPeerIds.SetEquals(hashSet))
		{
			_configuredPeerIds.Clear();
			_configuredPeerIds.UnionWith(hashSet);
			OnPropertyChanged("AuthorizedCount");
			OnPropertyChanged("ShowNoAuthorizationWarning");
		}
		if (_configurationPending != pendingRestart)
		{
			_configurationPending = pendingRestart;
		}
		OnPropertyChanged("AuthorizationSummary");
		OnPropertyChanged("ConfiguredAuthorizationSummary");
	}

	public void MarkAuthorizationEffective(IEnumerable<string> peerIds)
	{
		AuthorizedPeerIds.Clear();
		AuthorizedPeerIds.UnionWith(peerIds);
		_effectiveKnown = true;
		_configurationPending = false;
		OnPropertyChanged("EffectiveAuthorizationSummary");
		OnPropertyChanged("ConfiguredAuthorizationSummary");
		OnPropertyChanged("AuthorizationSummary");
	}

	public void InitializeEffectiveAuthorization(IEnumerable<string> peerIds, bool known)
	{
		AuthorizedPeerIds.Clear();
		AuthorizedPeerIds.UnionWith(peerIds);
		_effectiveKnown = known;
		OnPropertyChanged("EffectiveAuthorizationSummary");
	}

	public async Task RefreshAsync(CancellationToken ct = default(CancellationToken))
	{
		bool portOpen = await ControlApiClient.IsPortOpenAsync(400, ct).ConfigureAwait(continueOnCapturedContext: true);
		bool processAlive = (AgentRunning = ScheduledTaskService.IsAgentProcessRunning());
		ApiHealthy = false;
		TaskStateText = await _task.GetStateAsync(ct).ConfigureAwait(continueOnCapturedContext: true) switch
		{
			TaskState.NotInstalled => L("StatusTaskMissing"), 
			TaskState.Ready => L("StatusTaskReady"), 
			TaskState.Running => L("StatusTaskRunning"), 
			TaskState.Disabled => L("StatusTaskDisabled"), 
			_ => L("StatusTaskUnknown"), 
		};
		if (processAlive && portOpen)
		{
			AgentInfo agentInfo = await _api.GetInfoAsync(ct).ConfigureAwait(continueOnCapturedContext: true);
			if ((object)agentInfo != null)
			{
				PeerId = agentInfo.PeerId;
				AgentVersion = agentInfo.Version;
				AgentName = agentInfo.AgentName;
				SetAgentDetails(agentInfo);
				string expectedAgentVersion = RuntimeSecurityService.ExpectedAgentVersion;
				if (string.Equals(agentInfo.Version, expectedAgentVersion, StringComparison.Ordinal))
				{
					ApiHealthy = true;
					StatusHeadline = L("StatusProcessAndApiReady");
					StatusDetail = ((_configuredPeerIds.Count == 0) ? L("StatusProcessAndApiReadyNoAuth") : L("StatusProcessAndApiReadyAuth"));
				}
				else
				{
					StatusHeadline = L("StatusVersionMismatch");
					StatusDetail = F("StatusVersionMismatchDetail", agentInfo.Version, expectedAgentVersion);
				}
			}
			else
			{
				PeerId = "";
				AgentVersion = "";
				AgentName = "";
				SetAgentDetails(null);
				StatusHeadline = L("StatusApiUnavailable");
				StatusDetail = L("StatusApiUnavailableDetail");
			}
			if (ApiHealthy)
			{
				PeerQueryResult peerQueryResult = await _api.GetPeersResultAsync(ct).ConfigureAwait(continueOnCapturedContext: true);
				if (peerQueryResult.Success)
				{
					SyncPeers(peerQueryResult.Peers);
					ConnectivitySummary = BuildConnectivitySummary(peerQueryResult.Peers);
				}
				else
				{
					ConnectedPeers.Clear();
					ConnectivitySummary = F("StatusConnectionsUnavailable", peerQueryResult.ErrorCode);
				}
			}
			else
			{
				ConnectedPeers.Clear();
				ConnectivitySummary = "—";
			}
		}
		else
		{
			PeerId = "";
			AgentVersion = "";
			AgentName = "";
			SetAgentDetails(null);
			ConnectivitySummary = "—";
			ConnectedPeers.Clear();
			OnPropertyChanged("EffectiveAuthorizationSummary");
			StatusHeadline = (processAlive ? L("StatusProcessNoPort") : L("StatusNotRunning"));
			StatusDetail = (processAlive ? L("StatusProcessNoPortDetail") : L("StatusNotRunningDetail"));
		}
	}

	private string BuildConnectivitySummary(IReadOnlyList<PeerEntry> peers)
	{
		if (peers.Count == 0)
		{
			return L("StatusNoConnections");
		}
		string text = F("StatusConnectionsCount", peers.Count);
		if (_effectiveKnown && AuthorizedPeerIds.Count > 0)
		{
			int num = peers.Count((PeerEntry p) => AuthorizedPeerIds.Contains(p.PeerId));
			text += ((num > 0) ? F("StatusAuthorizedConnectionsCount", num) : L("StatusNoAuthorizedConnections"));
		}
		List<string> list = (from p in peers
			select p.ConnectionPath into p
			where p.Length > 0
			select p).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		if (list.Count > 0)
		{
			text += F("StatusConnectionPaths", string.Join(" · ", list));
		}
		return text;
	}

	private void SetAgentDetails(AgentInfo? info)
	{
		_relayReservationReady = info?.ReservationReady;
		_mdnsReady = info?.MdnsReady;
		_runningTasks = info?.RunningTasks;
		_availableTaskSlots = info?.AvailableTaskSlots;
		_listenAddressCount = info?.ListenAddresses.Count ?? 0;
		OnPropertyChanged("RelayReservationText");
		OnPropertyChanged("MdnsText");
		OnPropertyChanged("TaskCapacityText");
		OnPropertyChanged("ListenAddressesText");
	}

	private void SyncPeers(IReadOnlyList<PeerEntry> peers)
	{
		ConnectedPeers.Clear();
		foreach (PeerEntry peer in peers)
		{
			ConnectedPeers.Add(peer);
		}
	}

	private async Task StartAsync()
	{
		IsBusy = true;
		BusyMessage = L("StatusStarting");
		try
		{
			_ = 2;
			try
			{
				ProcessResult processResult = await _task.StopAsync().ConfigureAwait(continueOnCapturedContext: true);
				if (!processResult.Success)
				{
					ShowError(F("DialogStartFailed", processResult.CombinedOutput));
					return;
				}
				ProcessResult processResult2 = await _task.StartAsync().ConfigureAwait(continueOnCapturedContext: true);
				if (!processResult2.Success)
				{
					ShowError(F("DialogStartFailed", processResult2.CombinedOutput));
					return;
				}
				if (!(await InstallerService.WaitForReadyAsync(TimeSpan.FromSeconds(45.0)).ConfigureAwait(continueOnCapturedContext: true)))
				{
					ShowWarning(L("DialogStartTimeout"));
					return;
				}
				this.RuntimeRestartVerified?.Invoke(this, EventArgs.Empty);
			}
			catch (Exception ex)
			{
				ShowError(F("DialogStartFailed", ex.Message));
			}
		}
		finally
		{
			IsBusy = false;
			BusyMessage = "";
			await RefreshAsync().ConfigureAwait(continueOnCapturedContext: true);
		}
	}

	private async Task StopAsync()
	{
		if (AppDialog.ShowActions("DialogStopConfirm", "CommonStop", new AppDialogAction[2]
		{
			new AppDialogAction("StopAgent", "DialogActionStopAgent", AppDialogActionStyle.Danger),
			new AppDialogAction("Cancel", "CommonCancel", AppDialogActionStyle.Secondary, IsDefault: true, IsCancel: true)
		}, (MessageBoxImage)48) != "StopAgent")
		{
			return;
		}
		IsBusy = true;
		BusyMessage = L("StatusStopping");
		try
		{
			ProcessResult processResult = await _task.StopAsync().ConfigureAwait(continueOnCapturedContext: true);
			if (!processResult.Success)
			{
				ShowError(F("DialogStopFailed", processResult.CombinedOutput));
			}
		}
		catch (Exception ex)
		{
			ShowError(F("DialogStopFailed", ex.Message));
		}
		finally
		{
			IsBusy = false;
			BusyMessage = "";
			await RefreshAsync().ConfigureAwait(continueOnCapturedContext: true);
		}
	}

	private async Task RestartAsync()
	{
		IsBusy = true;
		BusyMessage = L("StatusRestarting");
		try
		{
			_ = 2;
			try
			{
				ProcessResult processResult = await _task.StopAsync().ConfigureAwait(continueOnCapturedContext: true);
				if (!processResult.Success)
				{
					ShowError(F("DialogRestartError", processResult.CombinedOutput));
					return;
				}
				ProcessResult processResult2 = await _task.StartAsync().ConfigureAwait(continueOnCapturedContext: true);
				if (!processResult2.Success)
				{
					ShowError(F("DialogRestartError", processResult2.CombinedOutput));
					return;
				}
				if (!(await InstallerService.WaitForReadyAsync(TimeSpan.FromSeconds(45.0)).ConfigureAwait(continueOnCapturedContext: true)))
				{
					ShowError(F("DialogRestartError", L("DialogStartTimeout")));
					return;
				}
				this.RuntimeRestartVerified?.Invoke(this, EventArgs.Empty);
			}
			catch (Exception ex)
			{
				ShowError(F("DialogRestartError", ex.Message));
			}
		}
		finally
		{
			IsBusy = false;
			BusyMessage = "";
			await RefreshAsync().ConfigureAwait(continueOnCapturedContext: true);
		}
	}

	private void CopyPeerId()
	{
		try
		{
			Clipboard.SetText(PeerId);
		}
		catch (Exception ex)
		{
			ShowError(F("DialogCopyFailed", ex.Message));
		}
	}

	private async Task RepairAsync()
	{
		if (AppDialog.ShowActions("DialogRepairConfirm", "StatusRepair", new AppDialogAction[2]
		{
			new AppDialogAction("Repair", "DialogActionRepair", AppDialogActionStyle.Primary),
			new AppDialogAction("Cancel", "CommonCancel", AppDialogActionStyle.Secondary, IsDefault: true, IsCancel: true)
		}, (MessageBoxImage)48) != "Repair")
		{
			return;
		}
		IsBusy = true;
		BusyMessage = L("StatusRepairing");
		try
		{
			IReadOnlyList<InstallStep> source = await new InstallerService().RepairAsync().ConfigureAwait(continueOnCapturedContext: true);
			bool flag = source.Any((InstallStep step) => step.Success && step.Kind == InstallStepKind.InstallationVerified);
			List<InstallStep> list = source.Where((InstallStep step) => step.Kind == InstallStepKind.CleanupWarning).ToList();
			List<InstallStep> list2 = source.Where((InstallStep step) => !step.Success && step.Kind != InstallStepKind.CleanupWarning).ToList();
			if (!flag || list2.Count > 0)
			{
				ShowWarning(F("DialogOperationFailed", string.Join(Environment.NewLine, list2.Select(InstallStepPresenter.FormatFailureWithTechnicalDetail))));
				return;
			}
			this.RuntimeRestartVerified?.Invoke(this, EventArgs.Empty);
			if (list.Count > 0)
			{
				ShowWarning(F("DialogRepairCleanupWarning", string.Join(Environment.NewLine, list.Select(InstallStepPresenter.FormatFailureWithTechnicalDetail))));
			}
		}
		catch (Exception ex)
		{
			ShowError(F("DialogOperationFailed", ex.Message));
		}
		finally
		{
			IsBusy = false;
			BusyMessage = "";
			await CheckDeploymentAsync().ConfigureAwait(continueOnCapturedContext: true);
			await RefreshAsync().ConfigureAwait(continueOnCapturedContext: true);
		}
	}

	public async Task CheckDeploymentAsync(CancellationToken ct = default(CancellationToken))
	{
		try
		{
			IReadOnlyList<DeploymentIssue> readOnlyList = await InstallerService.CheckDeploymentAsync(ct).ConfigureAwait(continueOnCapturedContext: true);
			DeploymentIssues = ((readOnlyList.Count == 0) ? "" : string.Join(Environment.NewLine, readOnlyList.Select((DeploymentIssue issue) => "· " + ((issue.Detail.Length == 0) ? L(issue.ResourceKey) : F(issue.ResourceKey, issue.Detail)))));
		}
		catch (Exception ex)
		{
			DeploymentIssues = F("StatusDeploymentCheckFailed", ex.Message);
		}
	}

	public void RefreshLanguage()
	{
		OnPropertyChanged("RunningText");
		OnPropertyChanged("AuthorizationSummary");
		OnPropertyChanged("ConfiguredAuthorizationSummary");
		OnPropertyChanged("EffectiveAuthorizationSummary");
		OnPropertyChanged("RelayReservationText");
		OnPropertyChanged("MdnsText");
		OnPropertyChanged("TaskCapacityText");
		OnPropertyChanged("ListenAddressesText");
	}

	private static void ShowError(string text)
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		AppDialog.Show(text, L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)16);
	}

	private static void ShowWarning(string text)
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		AppDialog.Show(text, L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)48);
	}
}
