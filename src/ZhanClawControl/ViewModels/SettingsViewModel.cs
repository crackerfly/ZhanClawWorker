#nullable disable warnings
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using ZhanClawControl.Infrastructure;
using ZhanClawControl.Localization;
using ZhanClawControl.Models;
using ZhanClawControl.Services;
using ZhanClawControl.Views.Dialogs;

namespace ZhanClawControl.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
	private const long MaxTransferMiBLimit = 1048576L;

	private readonly AgentConfigService _config = new AgentConfigService();

	private readonly ScheduledTaskService _task = new ScheduledTaskService();

	private readonly UiStateService _uiState = new UiStateService();

	private readonly InstallerService _installer = new InstallerService();

	private string _agentName = "";

	private string _agentTags = "";

	private string _bootstrapAddrs = "";

	private string _rendezvousGroup = "";

	private int _maxParallelTasks = 4;

	private long _maxTransferMiB = 8192L;

	private long _loadedMaxTransferBytes = 8589934592L;

	private bool _maxTransferEdited;

	private bool _autoStart = true;

	private bool _autoStartKnown;

	private bool _minimizeToTray = true;

	private bool _isBusy;

	private bool _loading;

	private bool _pendingRestart;

	private string _installedVersionText = "";

	private bool _installedVersionUnknown;

	private bool _agentNotInstalled;

	private string _selectedLanguage = "Auto";

	private bool _configurationLoaded;

	private string _configurationLoadFailureDetail = "";

	public AsyncRelayCommand SaveCommand { get; }

	public AsyncRelayCommand ReloadCommand { get; }

	public AsyncRelayCommand UninstallCommand { get; }

	public ObservableCollection<LanguageOption> LanguageOptions { get; } = new ObservableCollection<LanguageOption>();

	public string AgentName
	{
		get
		{
			return _agentName;
		}
		set
		{
			SetProperty(ref _agentName, value, "AgentName");
		}
	}

	public string AgentTags
	{
		get
		{
			return _agentTags;
		}
		set
		{
			SetProperty(ref _agentTags, value, "AgentTags");
		}
	}

	public string BootstrapAddrs
	{
		get
		{
			return _bootstrapAddrs;
		}
		set
		{
			SetProperty(ref _bootstrapAddrs, value, "BootstrapAddrs");
		}
	}

	public string RendezvousGroup
	{
		get
		{
			return _rendezvousGroup;
		}
		set
		{
			SetProperty(ref _rendezvousGroup, value, "RendezvousGroup");
		}
	}

	public int MaxParallelTasks
	{
		get
		{
			return _maxParallelTasks;
		}
		set
		{
			SetProperty(ref _maxParallelTasks, Math.Clamp(value, 1, 64), "MaxParallelTasks");
		}
	}

	public long MaxTransferMiB
	{
		get
		{
			return _maxTransferMiB;
		}
		set
		{
			if (SetProperty(ref _maxTransferMiB, Math.Clamp(value, 1L, 1048576L), "MaxTransferMiB") && !_loading)
			{
				_maxTransferEdited = true;
			}
		}
	}

	public bool AutoStart
	{
		get
		{
			return _autoStart;
		}
		set
		{
			SetProperty(ref _autoStart, value, "AutoStart");
		}
	}

	public bool AutoStartKnown
	{
		get
		{
			return _autoStartKnown;
		}
		private set
		{
			if (SetProperty(ref _autoStartKnown, value, "AutoStartKnown"))
			{
				SaveCommand.RaiseCanExecuteChanged();
			}
		}
	}

	public bool ConfigurationLoaded
	{
		get
		{
			return _configurationLoaded;
		}
		private set
		{
			if (SetProperty(ref _configurationLoaded, value, "ConfigurationLoaded"))
			{
				OnPropertyChanged("HasConfigurationLoadWarning");
				OnPropertyChanged("ConfigurationLoadWarning");
				SaveCommand.RaiseCanExecuteChanged();
			}
		}
	}

	public bool HasConfigurationLoadWarning => !ConfigurationLoaded;

	public string ConfigurationLoadWarning
	{
		get
		{
			if (!ConfigurationLoaded)
			{
				return F("SettingsConfigLoadFailed", _configurationLoadFailureDetail);
			}
			return "";
		}
	}

	public bool MinimizeToTray
	{
		get
		{
			return _minimizeToTray;
		}
		set
		{
			//IL_007d: Unknown result type (might be due to invalid IL or missing references)
			if (SetProperty(ref _minimizeToTray, value, "MinimizeToTray") && !_loading)
			{
				UiState uiState = _uiState.Load();
				uiState.MinimizeToTray = value;
				if (!_uiState.Save(uiState))
				{
					_minimizeToTray = !value;
					OnPropertyChanged("MinimizeToTray");
					AppDialog.Show(F("DialogSaveFailed", L("CommonUnknown")), L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)16);
				}
			}
		}
	}

	public string SelectedLanguage
	{
		get
		{
			return _selectedLanguage;
		}
		set
		{
			//IL_004b: Unknown result type (might be due to invalid IL or missing references)
			if (!_loading && !string.IsNullOrWhiteSpace(value) && SetProperty(ref _selectedLanguage, value, "SelectedLanguage") && !App.Localization.SetLanguage(value))
			{
				AppDialog.Show(L("DialogLanguageSaveFailed"), L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)48);
			}
		}
	}

	public string InstalledVersionText
	{
		get
		{
			return _installedVersionText;
		}
		private set
		{
			SetProperty(ref _installedVersionText, value, "InstalledVersionText");
		}
	}

	public string DataRootText => "C:\\ProgramData\\P2PAgent";

	public string InstallRootText => "C:\\Program Files\\P2PAgent";

	public bool PendingRestart
	{
		get
		{
			return _pendingRestart;
		}
		private set
		{
			SetProperty(ref _pendingRestart, value, "PendingRestart");
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
				SaveCommand.RaiseCanExecuteChanged();
				ReloadCommand.RaiseCanExecuteChanged();
				UninstallCommand.RaiseCanExecuteChanged();
			}
		}
	}

	public event EventHandler? UninstallCompleted;

	public event EventHandler? RuntimeRestartVerified;

	public SettingsViewModel()
	{
		SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy && ConfigurationLoaded && AutoStartKnown);
		ReloadCommand = new AsyncRelayCommand(LoadAsync, () => !IsBusy);
		UninstallCommand = new AsyncRelayCommand(UninstallAsync, () => !IsBusy);
		RefreshLanguageOptions();
	}

	private static string L(string key)
	{
		return App.Localization.Text(key);
	}

	private static string F(string key, params object?[] values)
	{
		return App.Localization.Format(key, values);
	}

	public async Task LoadAsync()
	{
		if (IsBusy)
		{
			return;
		}
		IsBusy = true;
		_loading = true;
		try
		{
			_selectedLanguage = App.Localization.SelectedLanguage;
			OnPropertyChanged("SelectedLanguage");
			UiState uiState = _uiState.Load();
			MinimizeToTray = uiState.MinimizeToTray;
			PendingRestart = uiState.ConfigurationPendingRestart;
			try
			{
				if (!_config.Exists)
				{
					throw new FileNotFoundException("agent-config.json 不存在。", AppPaths.ConfigFile);
				}
				JsonObject config = _config.Load();
				AgentConfigService.ValidateRuntimeBoundary(config);
				AgentName = AgentConfigService.GetString(config, "agent_name", Environment.MachineName);
				AgentTags = string.Join(", ", AgentConfigService.GetStringArray(config, "agent_tags"));
				BootstrapAddrs = string.Join(Environment.NewLine, AgentConfigService.GetStringArray(config, "bootstrap_addrs"));
				RendezvousGroup = AgentConfigService.GetString(config, "rendezvous_group", "p2p-agents");
				MaxParallelTasks = AgentConfigService.GetInt(config, "max_parallel_tasks", 4);
				_loadedMaxTransferBytes = AgentConfigService.GetLong(config, "max_transfer_bytes", 8589934592L);
				long num = Math.Clamp(_loadedMaxTransferBytes, 1L, 1099511627776L);
				MaxTransferMiB = Math.Clamp((num + 1048576 - 1) / 1048576, 1L, 1048576L);
				_maxTransferEdited = false;
				_configurationLoadFailureDetail = "";
				ConfigurationLoaded = true;
			}
			catch (Exception ex)
			{
				_configurationLoadFailureDetail = ex.Message;
				ConfigurationLoaded = false;
			}
			try
			{
				_agentNotInstalled = !File.Exists(AppPaths.AgentExe);
				if (_agentNotInstalled)
				{
					InstalledVersionText = L("CommonNotInstalled");
					_installedVersionUnknown = false;
				}
				else
				{
					await RuntimeSecurityService.ValidateAgentPayloadAsync(AppPaths.AgentExe).ConfigureAwait(continueOnCapturedContext: true);
					InstalledVersionText = RuntimeSecurityService.ExpectedAgentVersion;
					_installedVersionUnknown = false;
				}
			}
			catch
			{
				_agentNotInstalled = false;
				_installedVersionUnknown = true;
				InstalledVersionText = L("CommonUnknown");
			}
			ScheduledTaskInspection scheduledTaskInspection = await _task.InspectAsync().ConfigureAwait(continueOnCapturedContext: true);
			if (ConfigurationLoaded && !scheduledTaskInspection.QueryFailed && scheduledTaskInspection.Exists && scheduledTaskInspection.MatchesExpectedDefinition)
			{
				AutoStart = scheduledTaskInspection.EffectiveEnabled;
				AutoStartKnown = true;
			}
			else
			{
				AutoStartKnown = false;
			}
		}
		finally
		{
			_loading = false;
			IsBusy = false;
		}
	}

	private async Task SaveAsync()
	{
		if (!ConfigurationLoaded || !AutoStartKnown)
		{
			return;
		}
		IsBusy = true;
		bool configSaved = false;
		try
		{
			string text = AgentName.Trim();
			List<string> list = SplitLines(BootstrapAddrs);
			string rendezvousGroup = RendezvousGroup;
			int length = text.Length;
			bool flag = ((length < 1 || length > 128) ? true : false);
			bool flag2 = flag || text.Any(char.IsControl) || list.Count > 32 || list.Any((string address) => !AgentConfigService.LooksLikeBootstrapMultiaddr(address)) || !AgentConfigService.IsValidRendezvousGroup(rendezvousGroup);
			if (!flag2)
			{
				int maxParallelTasks = MaxParallelTasks;
				bool flag3 = ((maxParallelTasks < 1 || maxParallelTasks > 64) ? true : false);
				flag2 = flag3;
			}
			bool flag4 = flag2;
			if (!flag4)
			{
				long maxTransferMiB = MaxTransferMiB;
				bool flag3 = ((maxTransferMiB < 1 || maxTransferMiB > 1048576) ? true : false);
				flag4 = flag3;
			}
			if (flag4)
			{
				throw new InvalidDataException(L("DialogInvalidSettings"));
			}
			long num;
			if (!_maxTransferEdited)
			{
				long maxTransferMiB = _loadedMaxTransferBytes;
				if (maxTransferMiB >= 1 && maxTransferMiB <= 1099511627776L)
				{
					num = _loadedMaxTransferBytes;
					goto IL_018f;
				}
			}
			num = checked(MaxTransferMiB * 1024 * 1024);
			goto IL_018f;
			IL_018f:
			long num2 = num;
			JsonObject jsonObject = _config.Load();
			jsonObject["agent_name"] = text;
			AgentConfigService.SetStringArray(jsonObject, "agent_tags", SplitList(AgentTags, ','));
			AgentConfigService.SetStringArray(jsonObject, "bootstrap_addrs", list);
			jsonObject["rendezvous_group"] = rendezvousGroup;
			jsonObject["max_parallel_tasks"] = MaxParallelTasks;
			jsonObject["max_transfer_bytes"] = num2;
			AgentConfigService.ValidateAllowedPeers(jsonObject);
			UiState uiState = _uiState.Load();
			uiState.ConfigurationPendingRestart = true;
			if (!_uiState.Save(uiState, out string error))
			{
				throw new IOException(error ?? L("CommonUnknown"));
			}
			PendingRestart = true;
			_config.Save(jsonObject);
			configSaved = true;
			_loadedMaxTransferBytes = num2;
			_maxTransferEdited = false;
			ProcessResult processResult = await _task.SetEnabledAsync(AutoStart).ConfigureAwait(continueOnCapturedContext: true);
			if (!processResult.Success)
			{
				throw new InvalidOperationException(processResult.CombinedOutput);
			}
			if (!(AppDialog.ShowActions("DialogRestartNow", "DialogSaved", new AppDialogAction[2]
			{
				new AppDialogAction("RestartNow", "DialogActionRestartNow", AppDialogActionStyle.Primary),
				new AppDialogAction("Later", "DialogActionLater", AppDialogActionStyle.Secondary, IsDefault: true, IsCancel: true)
			}, (MessageBoxImage)64) == "RestartNow"))
			{
				return;
			}
			string restoreFailure = null;
			(bool Success, string Detail) restart;
			try
			{
				_ = 1;
				try
				{
					restart = await RestartAgentAsync().ConfigureAwait(continueOnCapturedContext: true);
				}
				catch (Exception ex)
				{
					restart = (Success: false, Detail: ex.Message);
				}
			}
			finally
			{
				try
				{
					ProcessResult processResult2 = await _task.SetEnabledAsync(AutoStart).ConfigureAwait(continueOnCapturedContext: true);
					if (!processResult2.Success)
					{
						restoreFailure = processResult2.CombinedOutput;
					}
				}
				catch (Exception ex2)
				{
					restoreFailure = ex2.Message;
				}
			}
			if (restart.Success)
			{
				this.RuntimeRestartVerified?.Invoke(this, EventArgs.Empty);
			}
			else
			{
				AppDialog.Show(F("DialogRestartFailed", restart.Detail), L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)48);
			}
			if (restoreFailure != null)
			{
				AutoStartKnown = false;
				AppDialog.Show(F("DialogAutoStartRestoreFailed", restoreFailure), L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)16);
			}
		}
		catch (OverflowException)
		{
			AppDialog.Show(L("DialogTransferOverflow"), L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)16);
		}
		catch (Exception ex4)
		{
			AppDialog.Show(F(configSaved ? "DialogApplyFailed" : "DialogSaveFailed", ex4.Message), L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)16);
		}
		finally
		{
			IsBusy = false;
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

	private async Task UninstallAsync()
	{
		if (AppDialog.ShowActions("DialogUninstallConfirm", "SettingsUninstall", new AppDialogAction[2]
		{
			new AppDialogAction("UninstallContinue", "DialogActionUninstallContinue", AppDialogActionStyle.Danger),
			new AppDialogAction("Cancel", "CommonCancel", AppDialogActionStyle.Secondary, IsDefault: true, IsCancel: true)
		}, (MessageBoxImage)48) != "UninstallContinue")
		{
			return;
		}
		string text = AppDialog.ShowActions("DialogRemoveData", "SettingsDataFolder", new AppDialogAction[3]
		{
			new AppDialogAction("KeepData", "DialogActionUninstallKeepData", AppDialogActionStyle.Primary, IsDefault: true),
			new AppDialogAction("DeleteData", "DialogActionUninstallDeleteData", AppDialogActionStyle.Danger),
			new AppDialogAction("Cancel", "CommonCancel", AppDialogActionStyle.Secondary, IsDefault: false, IsCancel: true)
		}, (MessageBoxImage)48);
		if ((!(text == "KeepData") && !(text == "DeleteData")) || 1 == 0)
		{
			return;
		}
		bool removeData = text == "DeleteData";
		IsBusy = true;
		try
		{
			IReadOnlyList<InstallStep> source = await _installer.UninstallAsync(removeData).ConfigureAwait(continueOnCapturedContext: true);
			List<InstallStep> list = source.Where((InstallStep s) => !s.Success).ToList();
			if (list.Count > 0)
			{
				AppDialog.Show(F("DialogUninstallPartial", string.Join(Environment.NewLine, list.Select(InstallStepPresenter.FormatFailureWithTechnicalDetail))), L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)48);
				return;
			}
			AppDialog.Show(L(source.Any((InstallStep step) => step.Success && step.RequiresDeferredCleanup) ? "DialogUninstalledDeferred" : "DialogUninstalled"), L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)64);
			this.UninstallCompleted?.Invoke(this, EventArgs.Empty);
		}
		catch (Exception ex)
		{
			AppDialog.Show(F("DialogOperationFailed", ex.Message), L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)16);
		}
		finally
		{
			IsBusy = false;
		}
	}

	public void RefreshLanguage()
	{
		RefreshLanguageOptions();
		OnPropertyChanged("ConfigurationLoadWarning");
		if (_installedVersionUnknown)
		{
			InstalledVersionText = L("CommonUnknown");
		}
		else if (_agentNotInstalled)
		{
			InstalledVersionText = L("CommonNotInstalled");
		}
	}

	public void MarkRuntimeApplied()
	{
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		UiState uiState = _uiState.Load();
		uiState.ConfigurationPendingRestart = false;
		if (!_uiState.Save(uiState, out string error))
		{
			AppDialog.Show(F("DialogRuntimeStateSaveFailed", error ?? L("CommonUnknown")), L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)48);
		}
		else
		{
			PendingRestart = false;
		}
	}

	private void RefreshLanguageOptions()
	{
		string selectedLanguage = _selectedLanguage;
		bool loading = _loading;
		_loading = true;
		try
		{
			LanguageOptions.Clear();
			foreach (LanguageOption option in App.Localization.GetOptions())
			{
				LanguageOptions.Add(option);
			}
			_selectedLanguage = selectedLanguage;
			OnPropertyChanged("SelectedLanguage");
		}
		finally
		{
			_loading = loading;
		}
	}

	public static List<string> SplitList(string text, char separator)
	{
		return text.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
	}

	public static List<string> SplitLines(string text)
	{
		return text.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
	}
}
