using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using ZhanClawControl.Infrastructure;
using ZhanClawControl.Localization;
using ZhanClawControl.Services;

namespace ZhanClawControl.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private const long MaxTransferMiBLimit = 1_048_576;
    private readonly AgentConfigService _config = new();
    private readonly ScheduledTaskService _task = new();
    private readonly UiStateService _uiState = new();
    private readonly InstallerService _installer = new();
    private string _agentName = "";
    private string _agentTags = "";
    private string _bootstrapAddrs = "";
    private string _rendezvousGroup = "";
    private int _maxParallelTasks = AppPaths.DefaultMaxParallelTasks;
    private long _maxTransferMiB = AppPaths.DefaultMaxTransferBytes / 1024 / 1024;
    private bool _autoStart = true;
    private bool _autoStartKnown;
    private bool _minimizeToTray = true;
    private bool _isBusy;
    private bool _loading;
    private bool _pendingRestart;
    private string _installedVersionText = "";
    private bool _installedVersionUnknown;
    private bool _agentNotInstalled;
    private string _selectedLanguage = LocalizationService.Auto;

    public SettingsViewModel()
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy && AutoStartKnown);
        ReloadCommand = new AsyncRelayCommand(LoadAsync, () => !IsBusy);
        UninstallCommand = new AsyncRelayCommand(UninstallAsync, () => !IsBusy);
        RefreshLanguageOptions();
    }

    private static string L(string key) => App.Localization.Text(key);
    private static string F(string key, params object?[] values) => App.Localization.Format(key, values);

    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand ReloadCommand { get; }
    public AsyncRelayCommand UninstallCommand { get; }
    public ObservableCollection<LanguageOption> LanguageOptions { get; } = new();
    public event EventHandler? UninstallCompleted;
    public event EventHandler? RuntimeRestartVerified;

    public string AgentName { get => _agentName; set => SetProperty(ref _agentName, value); }
    public string AgentTags { get => _agentTags; set => SetProperty(ref _agentTags, value); }
    public string BootstrapAddrs { get => _bootstrapAddrs; set => SetProperty(ref _bootstrapAddrs, value); }
    public string RendezvousGroup { get => _rendezvousGroup; set => SetProperty(ref _rendezvousGroup, value); }
    public int MaxParallelTasks { get => _maxParallelTasks; set => SetProperty(ref _maxParallelTasks, Math.Clamp(value, 1, 64)); }
    public long MaxTransferMiB { get => _maxTransferMiB; set => SetProperty(ref _maxTransferMiB, Math.Clamp(value, 1, MaxTransferMiBLimit)); }
    public bool AutoStart { get => _autoStart; set => SetProperty(ref _autoStart, value); }
    public bool AutoStartKnown
    {
        get => _autoStartKnown;
        private set
        {
            if (SetProperty(ref _autoStartKnown, value)) SaveCommand.RaiseCanExecuteChanged();
        }
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            if (!SetProperty(ref _minimizeToTray, value) || _loading) return;
            var state = _uiState.Load();
            state.MinimizeToTray = value;
            if (!_uiState.Save(state))
            {
                _minimizeToTray = !value;
                OnPropertyChanged();
                MessageBox.Show(F("DialogSaveFailed", L("CommonUnknown")), L("ProductName"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (_loading || string.IsNullOrWhiteSpace(value) || !SetProperty(ref _selectedLanguage, value)) return;
            if (!App.Localization.SetLanguage(value))
                MessageBox.Show(L("DialogLanguageSaveFailed"), L("ProductName"), MessageBoxButton.OK,
                    MessageBoxImage.Warning);
        }
    }

    public string InstalledVersionText { get => _installedVersionText; private set => SetProperty(ref _installedVersionText, value); }
    public string DataRootText => AppPaths.DataRoot;
    public string InstallRootText => AppPaths.InstallRoot;
    public bool PendingRestart { get => _pendingRestart; private set => SetProperty(ref _pendingRestart, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            SaveCommand.RaiseCanExecuteChanged();
            ReloadCommand.RaiseCanExecuteChanged();
            UninstallCommand.RaiseCanExecuteChanged();
        }
    }

    public async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _loading = true;
        try
        {
            var config = _config.Load();
            AgentName = AgentConfigService.GetString(config, "agent_name", Environment.MachineName + "-agent");
            AgentTags = string.Join(", ", AgentConfigService.GetStringArray(config, "agent_tags"));
            BootstrapAddrs = string.Join(Environment.NewLine, AgentConfigService.GetStringArray(config, "bootstrap_addrs"));
            RendezvousGroup = AgentConfigService.GetString(config, "rendezvous_group", AppPaths.DefaultRendezvousGroup);
            MaxParallelTasks = AgentConfigService.GetInt(config, "max_parallel_tasks", AppPaths.DefaultMaxParallelTasks);
            MaxTransferMiB = AgentConfigService.GetLong(config, "max_transfer_bytes", AppPaths.DefaultMaxTransferBytes) / 1024 / 1024;
            var state = _uiState.Load();
            MinimizeToTray = state.MinimizeToTray;
            _selectedLanguage = App.Localization.SelectedLanguage;
            OnPropertyChanged(nameof(SelectedLanguage));

            try
            {
                InstalledVersionText = File.Exists(AppPaths.AgentExe)
                    ? System.Diagnostics.FileVersionInfo.GetVersionInfo(AppPaths.AgentExe).FileVersion ?? L("CommonUnknown")
                    : L("CommonNotInstalled");
                _agentNotInstalled = !File.Exists(AppPaths.AgentExe);
                _installedVersionUnknown = !_agentNotInstalled && InstalledVersionText == L("CommonUnknown");
            }
            catch
            {
                _agentNotInstalled = false;
                _installedVersionUnknown = true;
                InstalledVersionText = L("CommonUnknown");
            }

            var inspection = await _task.InspectAsync().ConfigureAwait(true);
            if (!inspection.QueryFailed && inspection.Exists && inspection.MatchesExpectedDefinition)
            {
                AutoStart = ScheduledTaskService.ReadTaskEnabled(inspection.RawXml);
                AutoStartKnown = true;
            }
            else
            {
                AutoStartKnown = false;
            }
            PendingRestart = state.ConfigurationPendingRestart;
        }
        catch (Exception ex)
        {
            MessageBox.Show(F("DialogOperationFailed", ex.Message), L("ProductName"), MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _loading = false;
            IsBusy = false;
        }
    }

    private async Task SaveAsync()
    {
        if (!AutoStartKnown) return;
        IsBusy = true;
        var configSaved = false;
        try
        {
            var agentName = AgentName.Trim();
            var bootstrapAddrs = SplitLines(BootstrapAddrs);
            var rendezvousGroup = RendezvousGroup.Trim();
            if (agentName.Length is < 1 or > 128 || agentName.Any(char.IsControl) ||
                bootstrapAddrs.Count is < 1 or > 32 || bootstrapAddrs.Any(address => !LooksLikeBootstrapMultiaddr(address)) ||
                rendezvousGroup.Length is < 1 or > 128 || rendezvousGroup.Any(char.IsWhiteSpace) ||
                MaxParallelTasks is < 1 or > 64 || MaxTransferMiB is < 1 or > MaxTransferMiBLimit)
                throw new InvalidDataException(L("DialogInvalidSettings"));
            var transferBytes = checked(MaxTransferMiB * 1024L * 1024L);
            var config = _config.Load();
            config["agent_name"] = agentName;
            AgentConfigService.SetStringArray(config, "agent_tags", SplitList(AgentTags, ','));
            AgentConfigService.SetStringArray(config, "bootstrap_addrs", bootstrapAddrs);
            config["rendezvous_group"] = rendezvousGroup;
            config["max_parallel_tasks"] = MaxParallelTasks;
            config["max_transfer_bytes"] = transferBytes;
            AgentConfigService.ValidateAllowedPeers(config);

            var state = _uiState.Load();
            state.ConfigurationPendingRestart = true;
            if (!_uiState.Save(state, out var pendingError))
                throw new IOException(pendingError ?? L("CommonUnknown"));
            PendingRestart = true;
            _config.Save(config);
            configSaved = true;

            // Persist the sign-in preference before any runtime restart. The
            // scheduler service can start a disabled task on demand and restores
            // the preference after submitting /Run.
            var desiredState = await _task.SetEnabledAsync(AutoStart).ConfigureAwait(true);
            if (!desiredState.Success) throw new InvalidOperationException(desiredState.CombinedOutput);

            if (MessageBox.Show(L("DialogRestartNow"), L("DialogSaved"), MessageBoxButton.OKCancel,
                    MessageBoxImage.Information) == MessageBoxResult.OK)
            {
                (bool Success, string Detail) restart;
                string? restoreFailure = null;
                try
                {
                    restart = await RestartAgentAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    restart = (false, ex.Message);
                }
                finally
                {
                    try
                    {
                        var restored = await _task.SetEnabledAsync(AutoStart).ConfigureAwait(true);
                        if (!restored.Success) restoreFailure = restored.CombinedOutput;
                    }
                    catch (Exception ex)
                    {
                        restoreFailure = ex.Message;
                    }
                }

                if (restart.Success) RuntimeRestartVerified?.Invoke(this, EventArgs.Empty);
                else MessageBox.Show(F("DialogRestartFailed", restart.Detail), L("ProductName"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);

                if (restoreFailure is not null)
                {
                    AutoStartKnown = false;
                    MessageBox.Show(F("DialogAutoStartRestoreFailed", restoreFailure), L("ProductName"),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        catch (OverflowException)
        {
            MessageBox.Show(L("DialogTransferOverflow"), L("ProductName"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(F(configSaved ? "DialogApplyFailed" : "DialogSaveFailed", ex.Message),
                L("ProductName"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsBusy = false; }
    }

    private async Task<(bool Success, string Detail)> RestartAgentAsync()
    {
        var stop = await _task.StopAsync().ConfigureAwait(true);
        if (!stop.Success) return (false, stop.CombinedOutput);
        var start = await _task.StartAsync().ConfigureAwait(true);
        if (!start.Success) return (false, start.CombinedOutput);
        return await InstallerService.WaitForReadyAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(true)
            ? (true, "")
            : (false, L("DialogStartTimeout"));
    }

    private async Task UninstallAsync()
    {
        if (MessageBox.Show(L("DialogUninstallConfirm"), L("SettingsUninstall"), MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        var removeData = MessageBox.Show(L("DialogRemoveData"), L("SettingsDataFolder"), MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
        IsBusy = true;
        try
        {
            var steps = await _installer.UninstallAsync(removeData).ConfigureAwait(true);
            var failed = steps.Where(s => !s.Success).ToList();
            if (failed.Count > 0)
            {
                MessageBox.Show(F("DialogUninstallPartial", string.Join(Environment.NewLine,
                        failed.Select(step =>
                        {
                            var display = InstallStepPresenter.Present(step);
                            return $"· {display.Title}: {display.Detail}";
                        }))), L("ProductName"), MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            var deferred = steps.Any(step => step.Success &&
                (step.Title.Contains("安排重启后清理", StringComparison.Ordinal) ||
                 step.Title.Contains("安排退出后清理", StringComparison.Ordinal)));
            MessageBox.Show(L(deferred ? "DialogUninstalledDeferred" : "DialogUninstalled"),
                L("ProductName"), MessageBoxButton.OK, MessageBoxImage.Information);
            UninstallCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            MessageBox.Show(F("DialogOperationFailed", ex.Message), L("ProductName"), MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally { IsBusy = false; }
    }

    public void RefreshLanguage()
    {
        RefreshLanguageOptions();
        if (_installedVersionUnknown) InstalledVersionText = L("CommonUnknown");
        else if (_agentNotInstalled) InstalledVersionText = L("CommonNotInstalled");
    }

    public void MarkRuntimeApplied()
    {
        var state = _uiState.Load();
        state.ConfigurationPendingRestart = false;
        if (!_uiState.Save(state, out var error))
        {
            MessageBox.Show(F("DialogRuntimeStateSaveFailed", error ?? L("CommonUnknown")), L("ProductName"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        PendingRestart = false;
    }

    private void RefreshLanguageOptions()
    {
        var selected = _selectedLanguage;
        var wasLoading = _loading;
        _loading = true;
        try
        {
            LanguageOptions.Clear();
            foreach (var option in App.Localization.GetOptions()) LanguageOptions.Add(option);
            _selectedLanguage = selected;
            OnPropertyChanged(nameof(SelectedLanguage));
        }
        finally { _loading = wasLoading; }
    }

    public static List<string> SplitList(string text, char separator) =>
        text.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    public static List<string> SplitLines(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool LooksLikeBootstrapMultiaddr(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsWhiteSpace) ||
            !value.StartsWith("/", StringComparison.Ordinal)) return false;
        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6 || !parts.Contains("p2p", StringComparer.Ordinal)) return false;
        var p2p = Array.LastIndexOf(parts, "p2p");
        return p2p >= 0 && p2p + 1 < parts.Length && AgentConfigService.IsValidPeerId(parts[p2p + 1]);
    }
}
