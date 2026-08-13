using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using ZhanClawControl.Infrastructure;
using ZhanClawControl.Models;
using ZhanClawControl.Services;

namespace ZhanClawControl.ViewModels;

public sealed class AuthorizationChangedEventArgs : EventArgs
{
    public AuthorizationChangedEventArgs(bool runtimeVerified) => RuntimeVerified = runtimeVerified;
    public bool RuntimeVerified { get; }
}

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
    private readonly List<string> _effectivePeerIds = new();
    private bool _effectiveStateKnown;
    private bool _hasBackup;
    private bool _hasUnsavedChanges;
    private readonly List<string> _persistedPeerIds = new();

    public AuthorizationViewModel(ControlApiClient api)
    {
        _api = api;
        AddCommand = new RelayCommand(Add, CanAdd);
        RemoveCommand = new RelayCommand(Remove, () => !IsBusy && Selected is not null);
        PasteCommand = new RelayCommand(PasteFromClipboard, () => !IsBusy);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        RevokeAllCommand = new AsyncRelayCommand(RevokeAllAsync, () => !IsBusy && Peers.Count > 0);
        RestoreBackupCommand = new AsyncRelayCommand(RestoreBackupAsync, () => !IsBusy && HasBackup);
    }

    private static string L(string key) => App.Localization.Text(key);
    private static string F(string key, params object?[] values) => App.Localization.Format(key, values);

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
            if (!SetProperty(ref _newPeerId, value)) return;
            AddCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(NewPeerIdHint));
        }
    }
    public string NewNote { get => _newNote; set => SetProperty(ref _newNote, value); }
    public string NewPeerIdHint => NewPeerId.Trim().Length == 0
        ? L("AuthorizationPeerIdHintEmpty")
        : AgentConfigService.IsValidPeerId(NewPeerId.Trim())
            ? L("AuthorizationPeerIdValid")
            : L("AuthorizationPeerIdSuspicious");

    public AllowedPeerItem? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) RemoveCommand.RaiseCanExecuteChanged(); }
    }

    public bool PendingRestart
    {
        get => _pendingRestart;
        private set
        {
            if (SetProperty(ref _pendingRestart, value))
                AuthorizationChanged?.Invoke(this, new AuthorizationChangedEventArgs(false));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            AddCommand.RaiseCanExecuteChanged();
            RemoveCommand.RaiseCanExecuteChanged();
            PasteCommand.RaiseCanExecuteChanged();
            SaveCommand.RaiseCanExecuteChanged();
            RevokeAllCommand.RaiseCanExecuteChanged();
            RestoreBackupCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasBackup => _hasBackup;
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set => SetProperty(ref _hasUnsavedChanges, value);
    }
    public bool IsEmpty => Peers.Count == 0;
    public IReadOnlyList<string> EffectivePeerIds => _effectivePeerIds;
    public IReadOnlyList<string> ConfiguredPeerIds => _persistedPeerIds;
    public bool EffectiveStateKnown => _effectiveStateKnown;
    public event EventHandler<AuthorizationChangedEventArgs>? AuthorizationChanged;

    public void Load()
    {
        var config = _config.Load();
        var state = _uiStateService.Load();
        var notes = state.PeerNotes;
        _hasBackup = state.LastAllowedPeersBackup.Count > 0;
        UntrackAllPeers();
        Peers.Clear();
        foreach (var peerId in AgentConfigService.GetStringArray(config, "allowed_peers"))
        {
            var peer = new AllowedPeerItem
            {
                PeerId = peerId,
                Note = notes.TryGetValue(peerId, out var note) ? note : ""
            };
            TrackPeer(peer);
            Peers.Add(peer);
        }
        _persistedPeerIds.Clear();
        _persistedPeerIds.AddRange(Peers.Select(peer => peer.PeerId));
        HasUnsavedChanges = false;
        _effectivePeerIds.Clear();
        _effectivePeerIds.AddRange(state.EffectiveAllowedPeers);
        _effectiveStateKnown = state.EffectiveAllowedPeersKnown;
        var configuredIds = Peers.Select(peer => peer.PeerId).ToHashSet(StringComparer.Ordinal);
        _pendingRestart = state.AuthorizationPendingRestart ||
                          (_effectiveStateKnown &&
                           !configuredIds.SetEquals(_effectivePeerIds));
        OnPropertyChanged(nameof(PendingRestart));
        RaiseCollectionDependents();
        NotifyChanged(false);
    }

    public async Task RefreshOnlineStateAsync(CancellationToken ct = default)
    {
        if (!await ControlApiClient.IsPortOpenAsync(300, ct).ConfigureAwait(true))
        {
            foreach (var peer in Peers) peer.Online = false;
            return;
        }
        var connected = await _api.GetPeersAsync(ct).ConfigureAwait(true);
        var ids = connected.Select(p => p.PeerId).ToHashSet(StringComparer.Ordinal);
        foreach (var peer in Peers) peer.Online = ids.Contains(peer.PeerId);
    }

    private bool CanAdd()
    {
        var value = NewPeerId.Trim();
        return !IsBusy && AgentConfigService.IsValidPeerId(value) &&
               Peers.All(p => !string.Equals(p.PeerId, value, StringComparison.Ordinal));
    }

    private void Add()
    {
        if (!CanAdd()) return;
        var peer = new AllowedPeerItem { PeerId = NewPeerId.Trim(), Note = NewNote.Trim() };
        TrackPeer(peer);
        Peers.Add(peer);
        NewPeerId = "";
        NewNote = "";
        HasUnsavedChanges = true;
        RaiseCollectionDependents();
    }

    private void Remove()
    {
        if (IsBusy || Selected is null) return;
        var target = Selected;
        var label = target.Note.Length > 0 ? $"{target.Note} ({target.ShortPeerId})" : target.ShortPeerId;
        if (MessageBox.Show(F("DialogRemoveAuthorization", label), L("AuthorizationRemove"),
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        target.PropertyChanged -= OnPeerPropertyChanged;
        Peers.Remove(target);
        Selected = null;
        HasUnsavedChanges = true;
        RaiseCollectionDependents();
    }

    private void PasteFromClipboard()
    {
        if (IsBusy) return;
        try { if (Clipboard.ContainsText()) NewPeerId = Clipboard.GetText().Trim(); }
        catch { /* Clipboard can be temporarily locked by another process. */ }
    }

    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            PersistAuthorizationPending();
            PendingRestart = true;
            PersistToConfig(Peers.Select(p => p.PeerId).ToList());
            ReplacePersistedPeerIds(Peers.Select(peer => peer.PeerId));
            try
            {
                PersistNotes();
                HasUnsavedChanges = false;
            }
            catch (Exception ex)
            {
                // The security configuration is already committed, but note edits
                // remain a draft until their separate UI-state write succeeds.
                HasUnsavedChanges = true;
                ShowWarning(F("DialogNotesSaveFailed", ex.Message));
            }
            if (MessageBox.Show(L("DialogRestartNow"), L("DialogSaved"), MessageBoxButton.OKCancel,
                    MessageBoxImage.Information) == MessageBoxResult.OK)
            {
                var result = await RestartAgentAsync().ConfigureAwait(true);
                if (result.Success) MarkRuntimeApplied();
                else ShowWarning(F("DialogRestartFailed", result.Detail));
            }
            NotifyChanged(false);
        }
        catch (Exception ex) { ShowError(F("DialogSaveFailed", ex.Message)); }
        finally { IsBusy = false; }
    }

    private async Task RevokeAllAsync()
    {
        if (MessageBox.Show(L("DialogRevokeConfirm"), L("AuthorizationRevokeAll"),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        IsBusy = true;
        try
        {
            var state = _uiStateService.Load();
            state.LastAllowedPeersBackup = Peers.Select(p => p.PeerId).ToList();
            foreach (var peer in Peers)
            {
                if (peer.Note.Trim().Length > 0) state.PeerNotes[peer.PeerId] = peer.Note.Trim();
                else state.PeerNotes.Remove(peer.PeerId);
            }
            if (!_uiStateService.Save(state, out var backupError))
            {
                ShowError(F("DialogBackupFailed", backupError ?? L("CommonUnknown")));
                return;
            }
            _hasBackup = true;
            OnPropertyChanged(nameof(HasBackup));
            RestoreBackupCommand.RaiseCanExecuteChanged();

            PersistAuthorizationPending();
            PendingRestart = true;
            PersistToConfig(Array.Empty<string>());
            ReplacePersistedPeerIds(Array.Empty<string>());
            UntrackAllPeers();
            Peers.Clear();
            HasUnsavedChanges = false;
            RaiseCollectionDependents();
            NotifyChanged(false);

            var restart = await RestartAgentAsync().ConfigureAwait(true);
            if (restart.Success)
            {
                MarkRuntimeApplied();
                MessageBox.Show(L("DialogRevokeSuccess"), L("ProductName"), MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                ShowWarning(F("DialogRevokeFailed", restart.Detail));
            }
        }
        catch (Exception ex) { ShowError(F("DialogOperationFailed", ex.Message)); }
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
        var backup = state.LastAllowedPeersBackup.Distinct(StringComparer.Ordinal).ToList();
        if (backup.Count == 0) return;
        if (backup.Any(id => !AgentConfigService.IsValidPeerId(id)))
        {
            ShowError(L("DialogBackupInvalid"));
            return;
        }
        if (MessageBox.Show(F("DialogRestoreConfirm", backup.Count), L("AuthorizationRestore"),
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        IsBusy = true;
        try
        {
            PersistAuthorizationPending();
            PendingRestart = true;
            PersistToConfig(backup);
            ReplacePersistedPeerIds(backup);
            UntrackAllPeers();
            Peers.Clear();
            foreach (var peerId in backup)
            {
                var peer = new AllowedPeerItem
                {
                    PeerId = peerId,
                    Note = state.PeerNotes.TryGetValue(peerId, out var note) ? note : ""
                };
                TrackPeer(peer);
                Peers.Add(peer);
            }
            HasUnsavedChanges = false;
            RaiseCollectionDependents();
            NotifyChanged(false);

            var restart = await RestartAgentAsync().ConfigureAwait(true);
            if (restart.Success) MarkRuntimeApplied();
            else ShowWarning(F("DialogRestartFailed", restart.Detail));
        }
        catch (Exception ex) { ShowError(F("DialogOperationFailed", ex.Message)); }
        finally { IsBusy = false; }
    }

    private void PersistToConfig(IReadOnlyList<string> peerIds)
    {
        if (peerIds.Any(id => !AgentConfigService.IsValidPeerId(id)))
            throw new InvalidDataException(L("AuthorizationPeerIdSuspicious"));
        var config = _config.Load();
        AgentConfigService.SetStringArray(config, "allowed_peers", peerIds);
        _config.Save(config);
    }

    private void PersistNotes()
    {
        var state = _uiStateService.Load();
        foreach (var peer in Peers)
        {
            if (peer.Note.Trim().Length > 0) state.PeerNotes[peer.PeerId] = peer.Note.Trim();
            else state.PeerNotes.Remove(peer.PeerId);
        }
        if (!_uiStateService.Save(state, out var error))
            throw new IOException(error ?? L("CommonUnknown"));
    }

    private void PersistAuthorizationPending()
    {
        var state = _uiStateService.Load();
        state.AuthorizationPendingRestart = true;
        if (!_uiStateService.Save(state, out var error))
            throw new IOException(error ?? L("CommonUnknown"));
    }

    private async Task<(bool Success, string Detail)> RestartAgentAsync()
    {
        var stop = await _task.StopAsync().ConfigureAwait(true);
        if (!stop.Success) return (false, stop.CombinedOutput);
        var start = await _task.StartAsync().ConfigureAwait(true);
        if (!start.Success) return (false, start.CombinedOutput);
        var ready = await InstallerService.WaitForReadyAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(true);
        return ready ? (true, "") : (false, L("DialogStartTimeout"));
    }

    public void MarkRuntimeApplied()
    {
        // An unrelated verified restart must never promote an in-memory draft to
        // runtime-effective authorization. Only the last successfully saved list
        // can have been read by Agent.
        var effective = _persistedPeerIds.ToList();
        var state = _uiStateService.Load();
        state.EffectiveAllowedPeers = effective;
        state.EffectiveAllowedPeersKnown = true;
        state.AuthorizationPendingRestart = false;
        var persisted = _uiStateService.Save(state, out var error);
        _effectivePeerIds.Clear();
        _effectivePeerIds.AddRange(effective);
        _effectiveStateKnown = true;
        _pendingRestart = false;
        OnPropertyChanged(nameof(PendingRestart));
        NotifyChanged(true);
        if (!persisted)
            ShowWarning(F("DialogRuntimeStateSaveFailed", error ?? L("CommonUnknown")));
    }

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(NewPeerIdHint));
        foreach (var peer in Peers) peer.RefreshLanguage();
    }

    private void RaiseCollectionDependents()
    {
        OnPropertyChanged(nameof(IsEmpty));
        RevokeAllCommand.RaiseCanExecuteChanged();
        RestoreBackupCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasBackup));
        AddCommand.RaiseCanExecuteChanged();
    }

    private void NotifyChanged(bool runtimeVerified) =>
        AuthorizationChanged?.Invoke(this, new AuthorizationChangedEventArgs(runtimeVerified));

    private void ReplacePersistedPeerIds(IEnumerable<string> peerIds)
    {
        _persistedPeerIds.Clear();
        _persistedPeerIds.AddRange(peerIds);
    }

    private void TrackPeer(AllowedPeerItem peer) => peer.PropertyChanged += OnPeerPropertyChanged;

    private void UntrackAllPeers()
    {
        foreach (var peer in Peers) peer.PropertyChanged -= OnPeerPropertyChanged;
    }

    private void OnPeerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AllowedPeerItem.Note)) HasUnsavedChanges = true;
    }

    private static void ShowError(string text) => MessageBox.Show(text, L("ProductName"), MessageBoxButton.OK, MessageBoxImage.Error);
    private static void ShowWarning(string text) => MessageBox.Show(text, L("ProductName"), MessageBoxButton.OK, MessageBoxImage.Warning);
}
