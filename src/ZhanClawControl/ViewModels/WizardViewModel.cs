#nullable disable warnings
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using ZhanClawControl.Infrastructure;
using ZhanClawControl.Models;
using ZhanClawControl.Services;
using ZhanClawControl.Views.Dialogs;

namespace ZhanClawControl.ViewModels;

public sealed class WizardViewModel : ObservableObject
{
	private readonly InstallerService _installer = new InstallerService();

	private readonly UiStateService _uiState = new UiStateService();

	private int _step;

	private string _agentName = Environment.MachineName;

	private string _agentTags = "worker";

	private string _controllerPeerId = "";

	private string _controllerNote = "";

	private string _swarmKeyPath = "";

	private bool _installing;

	private bool _finished;

	private bool _succeeded;

	private bool _canRetry;

	private string _completionMessage = "";

	private string _runAsUser;

	public ObservableCollection<InstallStepDisplay> Steps { get; } = new ObservableCollection<InstallStepDisplay>();

	public RelayCommand NextCommand { get; }

	public RelayCommand BackCommand { get; }

	public RelayCommand BrowseSwarmKeyCommand { get; }

	public RelayCommand PastePeerIdCommand { get; }

	public AsyncRelayCommand InstallCommand { get; }

	public RelayCommand RetryCommand { get; }

	public RelayCommand FinishCommand { get; }

	public int Step
	{
		get
		{
			return _step;
		}
		private set
		{
			if (SetProperty(ref _step, value, "Step"))
			{
				OnPropertyChanged("IsStep0");
				OnPropertyChanged("IsStep1");
				OnPropertyChanged("IsStep2");
				OnPropertyChanged("ShowInstallButton");
				OnPropertyChanged("StepTitle");
				OnPropertyChanged("StepCaption");
				NextCommand.RaiseCanExecuteChanged();
				BackCommand.RaiseCanExecuteChanged();
			}
		}
	}

	public bool IsStep0 => Step == 0;

	public bool IsStep1 => Step == 1;

	public bool IsStep2 => Step == 2;

	public bool ShowInstallButton
	{
		get
		{
			if (IsStep2)
			{
				return !Finished;
			}
			return false;
		}
	}

	public string StepTitle => Step switch
	{
		0 => App.Localization.Text("WizardStepMachine"), 
		1 => App.Localization.Text("WizardStepAuthorization"), 
		_ => App.Localization.Text("WizardStepInstall"), 
	};

	public string StepCaption => Step switch
	{
		0 => App.Localization.Text("WizardCaptionMachine"), 
		1 => App.Localization.Text("WizardCaptionAuthorization"), 
		_ => App.Localization.Text("WizardCaptionInstall"), 
	};

	public string AgentName
	{
		get
		{
			return _agentName;
		}
		set
		{
			if (SetProperty(ref _agentName, value, "AgentName"))
			{
				NextCommand.RaiseCanExecuteChanged();
			}
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

	public string ControllerPeerId
	{
		get
		{
			return _controllerPeerId;
		}
		set
		{
			if (SetProperty(ref _controllerPeerId, value, "ControllerPeerId"))
			{
				OnPropertyChanged("PeerIdHint");
				NextCommand.RaiseCanExecuteChanged();
			}
		}
	}

	public string ControllerNote
	{
		get
		{
			return _controllerNote;
		}
		set
		{
			SetProperty(ref _controllerNote, value, "ControllerNote");
		}
	}

	public string PeerIdHint
	{
		get
		{
			string text = ControllerPeerId.Trim();
			if (text.Length == 0)
			{
				return App.Localization.Text("PeerHintEmpty");
			}
			if (text == "*")
			{
				return App.Localization.Text("PeerWildcardRejected");
			}
			if (!AllowedPeerItem.LooksLikePeerId(text))
			{
				return App.Localization.Text("AuthorizationPeerIdSuspicious");
			}
			return App.Localization.Text("AuthorizationPeerIdValid");
		}
	}

	public string SwarmKeyPath
	{
		get
		{
			return _swarmKeyPath;
		}
		set
		{
			if (SetProperty(ref _swarmKeyPath, value, "SwarmKeyPath"))
			{
				OnPropertyChanged("SwarmKeyStatus");
				NextCommand.RaiseCanExecuteChanged();
			}
		}
	}

	public bool HasEmbeddedSwarmKey => InstallerService.HasEmbeddedSwarmKey;

	public bool NeedsSwarmKeySelection => !HasEmbeddedSwarmKey;

	public string SwarmKeyStatus
	{
		get
		{
			if (HasEmbeddedSwarmKey)
			{
				return App.Localization.Text("WizardKeyEmbedded");
			}
			if (SwarmKeyPath.Trim().Length > 0)
			{
				if (!File.Exists(SwarmKeyPath))
				{
					return App.Localization.Text("WizardKeyMissingFile");
				}
				return App.Localization.Text("WizardKeySelected");
			}
			if (File.Exists(AppPaths.SwarmKeyFile))
			{
				return App.Localization.Text("WizardKeyExisting");
			}
			return App.Localization.Text("WizardKeyRequired");
		}
	}

	public bool SwarmKeyReady
	{
		get
		{
			if (!HasEmbeddedSwarmKey && (SwarmKeyPath.Trim().Length <= 0 || !File.Exists(SwarmKeyPath)))
			{
				return File.Exists(AppPaths.SwarmKeyFile);
			}
			return true;
		}
	}

	public string RunAsUser
	{
		get
		{
			return _runAsUser;
		}
		set
		{
			if (SetProperty(ref _runAsUser, value, "RunAsUser"))
			{
				NextCommand.RaiseCanExecuteChanged();
			}
		}
	}

	public bool HardenAcl => true;

	public bool Installing
	{
		get
		{
			return _installing;
		}
		private set
		{
			if (SetProperty(ref _installing, value, "Installing"))
			{
				InstallCommand.RaiseCanExecuteChanged();
				BackCommand.RaiseCanExecuteChanged();
			}
		}
	}

	public bool Finished
	{
		get
		{
			return _finished;
		}
		private set
		{
			if (SetProperty(ref _finished, value, "Finished"))
			{
				InstallCommand.RaiseCanExecuteChanged();
				NextCommand.RaiseCanExecuteChanged();
				BackCommand.RaiseCanExecuteChanged();
				OnPropertyChanged("ShowInstallButton");
				OnPropertyChanged("ShowRetryButton");
			}
		}
	}

	public bool Succeeded
	{
		get
		{
			return _succeeded;
		}
		private set
		{
			SetProperty(ref _succeeded, value, "Succeeded");
		}
	}

	public bool CanRetry
	{
		get
		{
			return _canRetry;
		}
		private set
		{
			if (SetProperty(ref _canRetry, value, "CanRetry"))
			{
				RetryCommand.RaiseCanExecuteChanged();
				OnPropertyChanged("ShowRetryButton");
			}
		}
	}

	public bool ShowRetryButton
	{
		get
		{
			if (Finished && !Succeeded)
			{
				return CanRetry;
			}
			return false;
		}
	}

	public string CompletionMessage
	{
		get
		{
			return _completionMessage;
		}
		private set
		{
			SetProperty(ref _completionMessage, value, "CompletionMessage");
		}
	}

	public event EventHandler<bool>? RequestClose;

	public WizardViewModel()
	{
		_runAsUser = App.InteractiveUserName;
		NextCommand = new RelayCommand(Next, CanGoNext);
		BackCommand = new RelayCommand(Back, () => Step > 0 && !Installing && !Finished);
		BrowseSwarmKeyCommand = new RelayCommand(BrowseSwarmKey);
		PastePeerIdCommand = new RelayCommand(PastePeerId);
		InstallCommand = new AsyncRelayCommand(InstallAsync, () => !Installing && !Finished);
		RetryCommand = new RelayCommand(PrepareRetry, () => CanRetry && !Installing);
		FinishCommand = new RelayCommand((Action)delegate
		{
			this.RequestClose?.Invoke(this, Succeeded);
		}, (Func<bool>?)null);
	}

	private bool CanGoNext()
	{
		if (Installing || Finished)
		{
			return false;
		}
		switch (Step)
		{
		case 0:
		{
			int length = AgentName.Trim().Length;
			return length > 0 && length <= 128 && !AgentName.Any(char.IsControl) && RunAsUser.Trim().Length > 0 && SwarmKeyReady;
		}
		case 1:
			return ControllerPeerId.Trim().Length == 0 || AgentConfigService.IsValidPeerId(ControllerPeerId.Trim());
		default:
			return false;
		}
	}

	private void Next()
	{
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		if (Step == 1 && ControllerPeerId.Trim().Length > 0 && !AgentConfigService.IsValidPeerId(ControllerPeerId.Trim()))
		{
			AppDialog.Show(App.Localization.Text("AuthorizationPeerIdSuspicious"), App.Localization.Text("AuthorizationTitle"), (MessageBoxButton)0, (MessageBoxImage)48);
		}
		else
		{
			Step = Math.Min(2, Step + 1);
		}
	}

	private void Back()
	{
		Step = Math.Max(0, Step - 1);
	}

	private void PrepareRetry()
	{
		if (CanRetry && !Installing)
		{
			Finished = false;
			CanRetry = false;
			CompletionMessage = "";
			Step = 0;
		}
	}

	private void BrowseSwarmKey()
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Expected O, but got Unknown
		OpenFileDialog val = new OpenFileDialog
		{
			Title = App.Localization.Text("WizardPrivateNetworkKey"),
			Filter = App.Localization.Text("FileFilterSwarmKey"),
			CheckFileExists = true
		};
		if (AppDialog.ShowFileDialog((CommonDialog)(object)val) == true)
		{
			SwarmKeyPath = ((FileDialog)val).FileName;
		}
	}

	private void PastePeerId()
	{
		try
		{
			if (Clipboard.ContainsText())
			{
				ControllerPeerId = Clipboard.GetText().Trim();
			}
		}
		catch
		{
		}
	}

	private async Task InstallAsync()
	{
		Installing = true;
		Steps.Clear();
		try
		{
			List<string> allowedPeers = new List<string>();
			string peerId = ControllerPeerId.Trim();
			if (peerId.Length > 0 && peerId != "*")
			{
				allowedPeers.Add(peerId);
			}
			InstallOptions options = new InstallOptions(AgentName.Trim(), SettingsViewModel.SplitList(AgentTags, ','), allowedPeers, AppPaths.DefaultBootstrapAddrs, "p2p-agents", 4, 8589934592L, RunAsUser.Trim(), (!HasEmbeddedSwarmKey && SwarmKeyPath.Trim().Length > 0) ? SwarmKeyPath.Trim() : null, HardenAcl: true);
			Progress<InstallStep> progress = new Progress<InstallStep>(delegate(InstallStep step)
			{
				Steps.Add(InstallStepPresenter.Present(step));
			});
			IReadOnlyList<InstallStep> source = await _installer.InstallAsync(options, progress).ConfigureAwait(continueOnCapturedContext: true);
			Succeeded = source.Any((InstallStep step) => step.Success && step.Kind == InstallStepKind.InstallationVerified);
			bool flag = source.Any((InstallStep step) => step.Kind == InstallStepKind.RollbackFailed);
			bool flag2 = source.Any((InstallStep step) => step.Success && step.Kind == InstallStepKind.RollbackSucceeded);
			bool flag3 = source.Any((InstallStep step) => step.Kind == InstallStepKind.NoMutationFailure);
			CanRetry = !Succeeded && !flag && (flag3 || flag2);
			CompletionMessage = ((!Succeeded) ? (flag ? App.Localization.Text("WizardInstallRollbackFailed") : App.Localization.Text("WizardInstallRetryAvailable")) : (source.Any((InstallStep step) => step.Kind == InstallStepKind.CleanupWarning) ? App.Localization.Text("WizardInstalledCleanupWarning") : App.Localization.Text("WizardInstalledSuccess")));
			if (Succeeded)
			{
				UiState uiState = _uiState.Load();
				if (peerId.Length > 0 && ControllerNote.Trim().Length > 0)
				{
					uiState.PeerNotes[peerId] = ControllerNote.Trim();
				}
				uiState.EffectiveAllowedPeers = allowedPeers.ToList();
				uiState.EffectiveAllowedPeersKnown = true;
				uiState.AuthorizationPendingRestart = false;
				uiState.ConfigurationPendingRestart = false;
				if (!_uiState.Save(uiState, out string error))
				{
					AppDialog.Show(App.Localization.Format("DialogWizardStateSaveFailed", error ?? App.Localization.Text("CommonUnknown")), App.Localization.Text("ProductName"), (MessageBoxButton)0, (MessageBoxImage)48);
				}
			}
		}
		catch (Exception ex)
		{
			Steps.Add(InstallStepPresenter.Present(new InstallStep("安装中断", Success: false, ex.Message)));
			Succeeded = false;
			CanRetry = true;
			CompletionMessage = App.Localization.Text("WizardInstallRetryAvailable");
		}
		finally
		{
			Installing = false;
			Finished = true;
		}
	}
}
