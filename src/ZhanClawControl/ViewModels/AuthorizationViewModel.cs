#nullable disable warnings
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ZhanClawControl.Infrastructure;
using ZhanClawControl.Models;
using ZhanClawControl.Services;
using ZhanClawControl.Views.Dialogs;

namespace ZhanClawControl.ViewModels;

public sealed class AuthorizationViewModel : ObservableObject
{
	private readonly AgentConfigService _config = new AgentConfigService();

	private readonly ScheduledTaskService _task = new ScheduledTaskService();

	private readonly UiStateService _uiStateService = new UiStateService();

	private readonly ControlApiClient _api;

	private string _newPeerId = "";

	private string _newNote = "";

	private AllowedPeerItem? _selected;

	private bool _pendingRestart;

	private bool _isBusy;

	private readonly List<string> _effectivePeerIds = new List<string>();

	private bool _effectiveStateKnown;

	private bool _hasBackup;

	private bool _hasUnsavedChanges;

	private string _loadWarning = "";

	private bool _configurationWritable = true;

	private readonly List<string> _persistedPeerIds = new List<string>();

	public ObservableCollection<AllowedPeerItem> Peers { get; } = new ObservableCollection<AllowedPeerItem>();

	public RelayCommand AddCommand { get; }

	public RelayCommand RemoveCommand { get; }

	public RelayCommand PasteCommand { get; }

	public AsyncRelayCommand SaveCommand { get; }

	public AsyncRelayCommand RevokeAllCommand { get; }

	public AsyncRelayCommand RestoreBackupCommand { get; }

	public string NewPeerId
	{
		get
		{
			return _newPeerId;
		}
		set
		{
			if (SetProperty(ref _newPeerId, value, "NewPeerId"))
			{
				AddCommand.RaiseCanExecuteChanged();
				OnPropertyChanged("NewPeerIdHint");
			}
		}
	}

	public string NewNote
	{
		get
		{
			return _newNote;
		}
		set
		{
			SetProperty(ref _newNote, value, "NewNote");
		}
	}

	public string NewPeerIdHint
	{
		get
		{
			if (NewPeerId.Trim().Length != 0)
			{
				if (!AgentConfigService.IsValidPeerId(NewPeerId.Trim()))
				{
					return L("AuthorizationPeerIdSuspicious");
				}
				return L("AuthorizationPeerIdValid");
			}
			return L("AuthorizationPeerIdHintEmpty");
		}
	}

	public AllowedPeerItem? Selected
	{
		get
		{
			return _selected;
		}
		set
		{
			if (SetProperty(ref _selected, value, "Selected"))
			{
				RemoveCommand.RaiseCanExecuteChanged();
			}
		}
	}

	public bool PendingRestart
	{
		get
		{
			return _pendingRestart;
		}
		private set
		{
			if (SetProperty(ref _pendingRestart, value, "PendingRestart"))
			{
				this.AuthorizationChanged?.Invoke(this, new AuthorizationChangedEventArgs(runtimeVerified: false));
			}
		}
	}

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
				RaiseCommandStates();
			}
		}
	}

	public bool HasBackup => _hasBackup;

	public bool HasUnsavedChanges
	{
		get
		{
			return _hasUnsavedChanges;
		}
		private set
		{
			SetProperty(ref _hasUnsavedChanges, value, "HasUnsavedChanges");
		}
	}

	public bool IsEmpty => Peers.Count == 0;

	public string LoadWarning
	{
		get
		{
			return _loadWarning;
		}
		private set
		{
			if (SetProperty(ref _loadWarning, value, "LoadWarning"))
			{
				OnPropertyChanged("HasLoadWarning");
			}
		}
	}

	public bool HasLoadWarning => LoadWarning.Length > 0;

	public bool ConfigurationWritable
	{
		get
		{
			return _configurationWritable;
		}
		private set
		{
			if (SetProperty(ref _configurationWritable, value, "ConfigurationWritable"))
			{
				RaiseCommandStates();
			}
		}
	}

	public IReadOnlyList<string> EffectivePeerIds => _effectivePeerIds;

	public IReadOnlyList<string> ConfiguredPeerIds => _persistedPeerIds;

	public bool EffectiveStateKnown => _effectiveStateKnown;

	public event EventHandler<AuthorizationChangedEventArgs>? AuthorizationChanged;

	public AuthorizationViewModel(ControlApiClient api)
	{
		_api = api;
		AddCommand = new RelayCommand(Add, CanAdd);
		RemoveCommand = new RelayCommand(Remove, () => ConfigurationWritable && !IsBusy && Selected != null);
		PasteCommand = new RelayCommand(PasteFromClipboard, () => ConfigurationWritable && !IsBusy);
		SaveCommand = new AsyncRelayCommand(SaveAsync, () => ConfigurationWritable && !IsBusy);
		RevokeAllCommand = new AsyncRelayCommand(RevokeAllAsync, () => ConfigurationWritable && !IsBusy && Peers.Count > 0);
		RestoreBackupCommand = new AsyncRelayCommand(RestoreBackupAsync, () => ConfigurationWritable && !IsBusy && HasBackup);
	}

	private static string L(string key)
	{
		return App.Localization.Text(key);
	}

	private static string F(string key, params object?[] values)
	{
		return App.Localization.Format(key, values);
	}

	public bool TryLoad(out string? error)
	{
		JsonObject config;
		try
		{
			config = _config.Load();
			AgentConfigService.ValidateRuntimeBoundary(config);
			LoadWarning = "";
			ConfigurationWritable = true;
			error = null;
		}
		catch (Exception ex)
		{
			config = AgentConfigService.CreateDefault();
			LoadWarning = F("AuthorizationConfigLoadFailed", ex.Message);
			ConfigurationWritable = false;
			error = ex.Message;
		}
		UiState uiState = _uiStateService.Load();
		Dictionary<string, string> peerNotes = uiState.PeerNotes;
		_hasBackup = uiState.LastAllowedPeersBackup.Count > 0;
		UntrackAllPeers();
		Peers.Clear();
		foreach (string item in AgentConfigService.GetStringArray(config, "allowed_peers"))
		{
			string value;
			AllowedPeerItem allowedPeerItem = new AllowedPeerItem
			{
				PeerId = item,
				Note = (peerNotes.TryGetValue(item, out value) ? value : "")
			};
			TrackPeer(allowedPeerItem);
			Peers.Add(allowedPeerItem);
		}
		_persistedPeerIds.Clear();
		_persistedPeerIds.AddRange(Peers.Select((AllowedPeerItem peer) => peer.PeerId));
		HasUnsavedChanges = false;
		_effectivePeerIds.Clear();
		_effectivePeerIds.AddRange(uiState.EffectiveAllowedPeers);
		_effectiveStateKnown = uiState.EffectiveAllowedPeersKnown;
		HashSet<string> hashSet = Peers.Select((AllowedPeerItem peer) => peer.PeerId).ToHashSet<string>(StringComparer.Ordinal);
		_pendingRestart = uiState.AuthorizationPendingRestart || (_effectiveStateKnown && !hashSet.SetEquals(_effectivePeerIds));
		OnPropertyChanged("PendingRestart");
		RaiseCollectionDependents();
		NotifyChanged(runtimeVerified: false);
		return error == null;
	}

	public async Task RefreshOnlineStateAsync(CancellationToken ct = default(CancellationToken))
	{
		if (!ScheduledTaskService.IsAgentProcessRunning())
		{
			foreach (AllowedPeerItem peer in Peers)
			{
				peer.Online = false;
			}
			return;
		}
		if (!(await ControlApiClient.IsPortOpenAsync(300, ct).ConfigureAwait(continueOnCapturedContext: true)))
		{
			foreach (AllowedPeerItem peer2 in Peers)
			{
				peer2.Online = null;
			}
			return;
		}
		PeerQueryResult peerQueryResult = await _api.GetPeersResultAsync(ct).ConfigureAwait(continueOnCapturedContext: true);
		if (!peerQueryResult.Success)
		{
			foreach (AllowedPeerItem peer3 in Peers)
			{
				peer3.Online = null;
			}
			return;
		}
		HashSet<string> hashSet = peerQueryResult.Peers.Select((PeerEntry p) => p.PeerId).ToHashSet<string>(StringComparer.Ordinal);
		foreach (AllowedPeerItem peer4 in Peers)
		{
			peer4.Online = hashSet.Contains(peer4.PeerId);
		}
	}

	private bool CanAdd()
	{
		string value = NewPeerId.Trim();
		if (ConfigurationWritable && !IsBusy && AgentConfigService.IsValidPeerId(value))
		{
			return Peers.All((AllowedPeerItem p) => !string.Equals(p.PeerId, value, StringComparison.Ordinal));
		}
		return false;
	}

	private void Add()
	{
		if (CanAdd())
		{
			AllowedPeerItem allowedPeerItem = new AllowedPeerItem
			{
				PeerId = NewPeerId.Trim(),
				Note = NewNote.Trim()
			};
			TrackPeer(allowedPeerItem);
			Peers.Add(allowedPeerItem);
			NewPeerId = "";
			NewNote = "";
			HasUnsavedChanges = true;
			RaiseCollectionDependents();
		}
	}

	private void Remove()
	{
		if (!IsBusy && Selected != null)
		{
			AllowedPeerItem selected = Selected;
			string text = ((selected.Note.Length > 0) ? (selected.Note + " (" + selected.ShortPeerId + ")") : selected.ShortPeerId);
			if (!(AppDialog.ShowActionsFormat("DialogRemoveAuthorization", new object[1] { text }, "AuthorizationRemove", new _003C_003Ez__ReadOnlyArray<AppDialogAction>(new AppDialogAction[2]
			{
				new AppDialogAction("Remove", "DialogActionRemove", AppDialogActionStyle.Danger),
				new AppDialogAction("Cancel", "CommonCancel", AppDialogActionStyle.Secondary, IsDefault: true, IsCancel: true)
			}), (MessageBoxImage)48) != "Remove"))
			{
				selected.PropertyChanged -= OnPeerPropertyChanged;
				Peers.Remove(selected);
				Selected = null;
				HasUnsavedChanges = true;
				RaiseCollectionDependents();
			}
		}
	}

	private void PasteFromClipboard()
	{
		if (IsBusy)
		{
			return;
		}
		try
		{
			if (Clipboard.ContainsText())
			{
				NewPeerId = Clipboard.GetText().Trim();
			}
		}
		catch
		{
		}
	}

	private async Task SaveAsync()
	{
		IsBusy = true;
		try
		{
			PersistAuthorizationPending();
			PendingRestart = true;
			PersistToConfig(Peers.Select((AllowedPeerItem p) => p.PeerId).ToList());
			ReplacePersistedPeerIds(Peers.Select((AllowedPeerItem peer) => peer.PeerId));
			try
			{
				PersistNotes();
				HasUnsavedChanges = false;
			}
			catch (Exception ex)
			{
				HasUnsavedChanges = true;
				ShowWarning(F("DialogNotesSaveFailed", ex.Message));
			}
			if (AppDialog.ShowActions("DialogRestartNow", "DialogSaved", new _003C_003Ez__ReadOnlyArray<AppDialogAction>(new AppDialogAction[2]
			{
				new AppDialogAction("RestartNow", "DialogActionRestartNow", AppDialogActionStyle.Primary),
				new AppDialogAction("Later", "DialogActionLater", AppDialogActionStyle.Secondary, IsDefault: true, IsCancel: true)
			}), (MessageBoxImage)64) == "RestartNow")
			{
				(bool, string) tuple = await RestartAgentAsync().ConfigureAwait(continueOnCapturedContext: true);
				if (tuple.Item1)
				{
					MarkRuntimeApplied();
				}
				else
				{
					ShowWarning(F("DialogRestartFailed", tuple.Item2));
				}
			}
			NotifyChanged(runtimeVerified: false);
		}
		catch (Exception ex2)
		{
			ShowError(F("DialogSaveFailed", ex2.Message));
		}
		finally
		{
			IsBusy = false;
		}
	}

	private async Task RevokeAllAsync()
	{
		if (AppDialog.ShowActions("DialogRevokeConfirm", "AuthorizationRevokeAll", new _003C_003Ez__ReadOnlyArray<AppDialogAction>(new AppDialogAction[2]
		{
			new AppDialogAction("RevokeAll", "DialogActionRevokeAll", AppDialogActionStyle.Danger),
			new AppDialogAction("Cancel", "CommonCancel", AppDialogActionStyle.Secondary, IsDefault: true, IsCancel: true)
		}), (MessageBoxImage)48) != "RevokeAll")
		{
			return;
		}
		IsBusy = true;
		try
		{
			UiState uiState = _uiStateService.Load();
			uiState.LastAllowedPeersBackup = Peers.Select((AllowedPeerItem p) => p.PeerId).ToList();
			foreach (AllowedPeerItem peer in Peers)
			{
				if (peer.Note.Trim().Length > 0)
				{
					uiState.PeerNotes[peer.PeerId] = peer.Note.Trim();
				}
				else
				{
					uiState.PeerNotes.Remove(peer.PeerId);
				}
			}
			if (!_uiStateService.Save(uiState, out string error))
			{
				ShowError(F("DialogBackupFailed", error ?? L("CommonUnknown")));
				return;
			}
			_hasBackup = true;
			OnPropertyChanged("HasBackup");
			RestoreBackupCommand.RaiseCanExecuteChanged();
			PersistAuthorizationPending();
			PendingRestart = true;
			PersistToConfig(Array.Empty<string>());
			ReplacePersistedPeerIds(Array.Empty<string>());
			UntrackAllPeers();
			Peers.Clear();
			HasUnsavedChanges = false;
			RaiseCollectionDependents();
			NotifyChanged(runtimeVerified: false);
			(bool, string) tuple = await RestartAgentAsync().ConfigureAwait(continueOnCapturedContext: true);
			if (tuple.Item1)
			{
				MarkRuntimeApplied();
				AppDialog.Show(L("DialogRevokeSuccess"), L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)64);
			}
			else
			{
				ShowWarning(F("DialogRevokeFailed", tuple.Item2));
			}
		}
		catch (Exception ex)
		{
			ShowError(F("DialogOperationFailed", ex.Message));
		}
		finally
		{
			IsBusy = false;
			OnPropertyChanged("HasBackup");
			RestoreBackupCommand.RaiseCanExecuteChanged();
		}
	}

	private async Task RestoreBackupAsync()
	{
		UiState uiState = _uiStateService.Load();
		List<string> list = uiState.LastAllowedPeersBackup.Distinct<string>(StringComparer.Ordinal).ToList();
		if (list.Count == 0)
		{
			return;
		}
		if (list.Any((string id) => !AgentConfigService.IsValidPeerId(id)))
		{
			ShowError(L("DialogBackupInvalid"));
		}
		else
		{
			if (AppDialog.ShowActionsFormat("DialogRestoreConfirm", new object[1] { list.Count }, "AuthorizationRestore", new _003C_003Ez__ReadOnlyArray<AppDialogAction>(new AppDialogAction[2]
			{
				new AppDialogAction("RestoreRestart", "DialogActionRestoreRestart", AppDialogActionStyle.Primary),
				new AppDialogAction("Cancel", "CommonCancel", AppDialogActionStyle.Secondary, IsDefault: true, IsCancel: true)
			}), (MessageBoxImage)32) != "RestoreRestart")
			{
				return;
			}
			IsBusy = true;
			try
			{
				PersistAuthorizationPending();
				PendingRestart = true;
				PersistToConfig(list);
				ReplacePersistedPeerIds(list);
				UntrackAllPeers();
				Peers.Clear();
				foreach (string item in list)
				{
					string value;
					AllowedPeerItem allowedPeerItem = new AllowedPeerItem
					{
						PeerId = item,
						Note = (uiState.PeerNotes.TryGetValue(item, out value) ? value : "")
					};
					TrackPeer(allowedPeerItem);
					Peers.Add(allowedPeerItem);
				}
				HasUnsavedChanges = false;
				RaiseCollectionDependents();
				NotifyChanged(runtimeVerified: false);
				(bool, string) tuple = await RestartAgentAsync().ConfigureAwait(continueOnCapturedContext: true);
				if (tuple.Item1)
				{
					MarkRuntimeApplied();
					return;
				}
				ShowWarning(F("DialogRestartFailed", tuple.Item2));
			}
			catch (Exception ex)
			{
				ShowError(F("DialogOperationFailed", ex.Message));
			}
			finally
			{
				IsBusy = false;
			}
		}
	}

	private void PersistToConfig(IReadOnlyList<string> peerIds)
	{
		if (peerIds.Any((string id) => !AgentConfigService.IsValidPeerId(id)))
		{
			throw new InvalidDataException(L("AuthorizationPeerIdSuspicious"));
		}
		JsonObject config = _config.Load();
		AgentConfigService.SetStringArray(config, "allowed_peers", peerIds);
		_config.Save(config);
	}

	private void PersistNotes()
	{
		UiState uiState = _uiStateService.Load();
		foreach (AllowedPeerItem peer in Peers)
		{
			if (peer.Note.Trim().Length > 0)
			{
				uiState.PeerNotes[peer.PeerId] = peer.Note.Trim();
			}
			else
			{
				uiState.PeerNotes.Remove(peer.PeerId);
			}
		}
		if (!_uiStateService.Save(uiState, out string error))
		{
			throw new IOException(error ?? L("CommonUnknown"));
		}
	}

	private void PersistAuthorizationPending()
	{
		UiState uiState = _uiStateService.Load();
		uiState.AuthorizationPendingRestart = true;
		if (!_uiStateService.Save(uiState, out string error))
		{
			throw new IOException(error ?? L("CommonUnknown"));
		}
	}

	private async Task<(bool Success, string Detail)> RestartAgentAsync()
	{
		ProcessResult processResult = await _task.StopAsync().ConfigureAwait(continueOnCapturedContext: true);
		if (!processResult.Success)
		{
			return (Success: false, Detail: processResult.CombinedOutput);
		}
		ProcessResult processResult2 = await _task.StartAsync().ConfigureAwait(continueOnCapturedContext: true);
		if (!processResult2.Success)
		{
			return (Success: false, Detail: processResult2.CombinedOutput);
		}
		return (await InstallerService.WaitForReadyAsync(TimeSpan.FromSeconds(45.0)).ConfigureAwait(continueOnCapturedContext: true)) ? (Success: true, Detail: "") : (Success: false, Detail: L("DialogStartTimeout"));
	}

	public void MarkRuntimeApplied()
	{
		List<string> list = _persistedPeerIds.ToList();
		UiState uiState = _uiStateService.Load();
		uiState.EffectiveAllowedPeers = list;
		uiState.EffectiveAllowedPeersKnown = true;
		uiState.AuthorizationPendingRestart = false;
		string error;
		bool num = _uiStateService.Save(uiState, out error);
		_effectivePeerIds.Clear();
		_effectivePeerIds.AddRange(list);
		_effectiveStateKnown = true;
		_pendingRestart = false;
		OnPropertyChanged("PendingRestart");
		NotifyChanged(runtimeVerified: true);
		if (!num)
		{
			ShowWarning(F("DialogRuntimeStateSaveFailed", error ?? L("CommonUnknown")));
		}
	}

	public void RefreshLanguage()
	{
		OnPropertyChanged("NewPeerIdHint");
		foreach (AllowedPeerItem peer in Peers)
		{
			peer.RefreshLanguage();
		}
	}

	private void RaiseCollectionDependents()
	{
		OnPropertyChanged("IsEmpty");
		RevokeAllCommand.RaiseCanExecuteChanged();
		RestoreBackupCommand.RaiseCanExecuteChanged();
		OnPropertyChanged("HasBackup");
		AddCommand.RaiseCanExecuteChanged();
	}

	private void RaiseCommandStates()
	{
		AddCommand.RaiseCanExecuteChanged();
		RemoveCommand.RaiseCanExecuteChanged();
		PasteCommand.RaiseCanExecuteChanged();
		SaveCommand.RaiseCanExecuteChanged();
		RevokeAllCommand.RaiseCanExecuteChanged();
		RestoreBackupCommand.RaiseCanExecuteChanged();
	}

	private void NotifyChanged(bool runtimeVerified)
	{
		this.AuthorizationChanged?.Invoke(this, new AuthorizationChangedEventArgs(runtimeVerified));
	}

	private void ReplacePersistedPeerIds(IEnumerable<string> peerIds)
	{
		_persistedPeerIds.Clear();
		_persistedPeerIds.AddRange(peerIds);
	}

	private void TrackPeer(AllowedPeerItem peer)
	{
		peer.PropertyChanged += OnPeerPropertyChanged;
	}

	private void UntrackAllPeers()
	{
		foreach (AllowedPeerItem peer in Peers)
		{
			peer.PropertyChanged -= OnPeerPropertyChanged;
		}
	}

	private void OnPeerPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == "Note")
		{
			HasUnsavedChanges = true;
		}
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
