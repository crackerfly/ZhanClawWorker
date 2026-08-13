using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using ZhanClawControl.Infrastructure;
using ZhanClawControl.Models;
using ZhanClawControl.Services;

namespace ZhanClawControl.ViewModels;

/// <summary>
/// 首次安装向导，完整替代 02-install-worker.cmd。
/// 三步：本机信息 → 授权主控 → 执行安装。
/// </summary>
public sealed class WizardViewModel : ObservableObject
{
    private readonly InstallerService _installer = new();
    private readonly UiStateService _uiState = new();

    private int _step;
    private string _agentName = Environment.MachineName + "-agent";
    private string _agentTags = "agent";
    private string _controllerPeerId = "";
    private string _controllerNote = "";
    private string _swarmKeyPath = "";
    private bool _installing;
    private bool _finished;
    private bool _succeeded;
    private string _runAsUser;

    public WizardViewModel()
    {
        _runAsUser = App.InteractiveUserName;
        NextCommand = new RelayCommand(Next, CanGoNext);
        BackCommand = new RelayCommand(Back, () => Step > 0 && !Installing);
        BrowseSwarmKeyCommand = new RelayCommand(BrowseSwarmKey);
        PastePeerIdCommand = new RelayCommand(PastePeerId);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => !Installing && !Finished);
        FinishCommand = new RelayCommand(() => RequestClose?.Invoke(this, Succeeded));
    }

    public ObservableCollection<InstallStepDisplay> Steps { get; } = new();

    public RelayCommand NextCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand BrowseSwarmKeyCommand { get; }
    public RelayCommand PastePeerIdCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public RelayCommand FinishCommand { get; }

    /// <summary>参数二为 true 表示安装成功，主窗口应继续启动。</summary>
    public event EventHandler<bool>? RequestClose;

    public int Step
    {
        get => _step;
        private set
        {
            if (SetProperty(ref _step, value))
            {
                OnPropertyChanged(nameof(IsStep0));
                OnPropertyChanged(nameof(IsStep1));
                OnPropertyChanged(nameof(IsStep2));
                OnPropertyChanged(nameof(StepTitle));
                OnPropertyChanged(nameof(StepCaption));
                NextCommand.RaiseCanExecuteChanged();
                BackCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsStep0 => Step == 0;
    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;

    public string StepTitle => Step switch
    {
        0 => App.Localization.Text("WizardStepMachine"),
        1 => App.Localization.Text("WizardStepAuthorization"),
        _ => App.Localization.Text("WizardStepInstall")
    };

    public string StepCaption => Step switch
    {
        0 => App.Localization.Text("WizardCaptionMachine"),
        1 => App.Localization.Text("WizardCaptionAuthorization"),
        _ => App.Localization.Text("WizardCaptionInstall")
    };

    public string AgentName
    {
        get => _agentName;
        set
        {
            if (SetProperty(ref _agentName, value))
            {
                NextCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string AgentTags
    {
        get => _agentTags;
        set => SetProperty(ref _agentTags, value);
    }

    public string ControllerPeerId
    {
        get => _controllerPeerId;
        set
        {
            if (SetProperty(ref _controllerPeerId, value))
            {
                OnPropertyChanged(nameof(PeerIdHint));
                NextCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ControllerNote
    {
        get => _controllerNote;
        set => SetProperty(ref _controllerNote, value);
    }

    public string PeerIdHint
    {
        get
        {
            var value = ControllerPeerId.Trim();
            if (value.Length == 0)
            {
                return App.Localization.Text("PeerHintEmpty");
            }

            if (value == "*")
            {
                return App.Localization.Text("PeerWildcardRejected");
            }

            return AllowedPeerItem.LooksLikePeerId(value)
                ? App.Localization.Text("AuthorizationPeerIdValid")
                : App.Localization.Text("AuthorizationPeerIdSuspicious");
        }
    }

    public string SwarmKeyPath
    {
        get => _swarmKeyPath;
        set
        {
            if (SetProperty(ref _swarmKeyPath, value))
            {
                OnPropertyChanged(nameof(SwarmKeyStatus));
                NextCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasEmbeddedSwarmKey => InstallerService.HasEmbeddedSwarmKey;

    /// <summary>内置了 swarm.key 时不再让用户选择文件，只显示一行确认。</summary>
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
                return File.Exists(SwarmKeyPath)
                    ? App.Localization.Text("WizardKeySelected")
                    : App.Localization.Text("WizardKeyMissingFile");
            }

            if (File.Exists(AppPaths.SwarmKeyFile))
            {
                return App.Localization.Text("WizardKeyExisting");
            }

            return App.Localization.Text("WizardKeyRequired");
        }
    }

    public bool SwarmKeyReady =>
        HasEmbeddedSwarmKey ||
        (SwarmKeyPath.Trim().Length > 0 && File.Exists(SwarmKeyPath)) ||
        File.Exists(AppPaths.SwarmKeyFile);

    public string RunAsUser
    {
        get => _runAsUser;
        set
        {
            if (SetProperty(ref _runAsUser, value)) NextCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HardenAcl => true;

    public bool Installing
    {
        get => _installing;
        private set
        {
            if (SetProperty(ref _installing, value))
            {
                InstallCommand.RaiseCanExecuteChanged();
                BackCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool Finished
    {
        get => _finished;
        private set
        {
            if (SetProperty(ref _finished, value))
            {
                InstallCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool Succeeded
    {
        get => _succeeded;
        private set => SetProperty(ref _succeeded, value);
    }

    private bool CanGoNext()
    {
        if (Installing)
        {
            return false;
        }

        return Step switch
        {
            0 => AgentName.Trim().Length is > 0 and <= 128 &&
                 !AgentName.Any(char.IsControl) &&
                 RunAsUser.Trim().Length > 0 && SwarmKeyReady,
            1 => ControllerPeerId.Trim().Length == 0 ||
                 AgentConfigService.IsValidPeerId(ControllerPeerId.Trim()),
            _ => false
        };
    }

    private void Next()
    {
        if (Step == 1 && ControllerPeerId.Trim().Length > 0 &&
            !AgentConfigService.IsValidPeerId(ControllerPeerId.Trim()))
        {
            MessageBox.Show(
                App.Localization.Text("AuthorizationPeerIdSuspicious"),
                App.Localization.Text("AuthorizationTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Step = Math.Min(2, Step + 1);
    }

    private void Back() => Step = Math.Max(0, Step - 1);

    private void BrowseSwarmKey()
    {
        var dialog = new OpenFileDialog
        {
            Title = App.Localization.Text("WizardPrivateNetworkKey"),
            Filter = App.Localization.Text("FileFilterSwarmKey"),
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            SwarmKeyPath = dialog.FileName;
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
            // 剪贴板被占用
        }
    }

    private async Task InstallAsync()
    {
        Installing = true;
        Steps.Clear();

        try
        {
            var allowedPeers = new List<string>();
            var peerId = ControllerPeerId.Trim();
            if (peerId.Length > 0 && peerId != "*")
            {
                allowedPeers.Add(peerId);
            }

            var options = new InstallOptions(
                AgentName.Trim(),
                SettingsViewModel.SplitList(AgentTags, ','),
                allowedPeers,
                AppPaths.DefaultBootstrapAddrs,
                AppPaths.DefaultRendezvousGroup,
                AppPaths.DefaultMaxParallelTasks,
                AppPaths.DefaultMaxTransferBytes,
                RunAsUser.Trim(),
                !HasEmbeddedSwarmKey && SwarmKeyPath.Trim().Length > 0 ? SwarmKeyPath.Trim() : null,
                true);

            var progress = new Progress<InstallStep>(step => Steps.Add(InstallStepPresenter.Present(step)));
            var result = await _installer.InstallAsync(options, progress).ConfigureAwait(true);

            Succeeded = result.Count > 0 &&
                        result.All(s => s.Success) &&
                        result.Any(s => s.Success &&
                            string.Equals(s.Title, "启动并验证 Agent", StringComparison.Ordinal));

            if (Succeeded)
            {
                var state = _uiState.Load();
                if (peerId.Length > 0 && ControllerNote.Trim().Length > 0)
                    state.PeerNotes[peerId] = ControllerNote.Trim();
                state.EffectiveAllowedPeers = allowedPeers.ToList();
                state.EffectiveAllowedPeersKnown = true;
                state.AuthorizationPendingRestart = false;
                state.ConfigurationPendingRestart = false;
                if (!_uiState.Save(state, out var noteError))
                {
                    MessageBox.Show(
                        App.Localization.Format("DialogWizardStateSaveFailed", noteError ?? App.Localization.Text("CommonUnknown")),
                        App.Localization.Text("ProductName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            Steps.Add(InstallStepPresenter.Present(new InstallStep("安装中断", false, ex.Message)));
            Succeeded = false;
        }
        finally
        {
            Installing = false;
            Finished = true;
        }
    }
}
