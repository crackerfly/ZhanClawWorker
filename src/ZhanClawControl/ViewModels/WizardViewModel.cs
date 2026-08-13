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
    private string _agentName = Environment.MachineName + "-worker";
    private string _agentTags = "worker";
    private string _controllerPeerId = "";
    private string _controllerNote = "";
    private string _swarmKeyPath = "";
    private bool _hardenAcl = true;
    private bool _installing;
    private bool _finished;
    private bool _succeeded;
    private string _runAsUser = InstallerService.CurrentUserName;

    public WizardViewModel()
    {
        NextCommand = new RelayCommand(Next, CanGoNext);
        BackCommand = new RelayCommand(Back, () => Step > 0 && !Installing);
        BrowseSwarmKeyCommand = new RelayCommand(BrowseSwarmKey);
        PastePeerIdCommand = new RelayCommand(PastePeerId);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => !Installing && !Finished);
        FinishCommand = new RelayCommand(() => RequestClose?.Invoke(this, Succeeded));
    }

    public ObservableCollection<InstallStep> Steps { get; } = new();

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
        0 => "本机信息",
        1 => "授权主控设备",
        _ => "安装"
    };

    public string StepCaption => Step switch
    {
        0 => "设置这台设备在 Fleet 中显示的名称，并指定私有网络密钥。",
        1 => "只有列入白名单的主控设备才能向本机提交任务。留空则安装后拒绝所有远端操作。",
        _ => "即将写入程序文件、配置与开机任务。全过程可见，失败会立即停止。"
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
                return "留空表示暂不授权任何主控设备（安全默认值，可在安装后随时添加）";
            }

            if (value == "*")
            {
                return "不允许使用通配符：那会授权私有网络中的所有成员控制本机";
            }

            return AllowedPeerItem.LooksLikePeerId(value)
                ? "格式看起来正常"
                : "格式可疑：PeerID 不含空格，长度通常为 52 个 base58 字符";
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
                return "已内置于安装包，无需手动选择。";
            }

            if (SwarmKeyPath.Trim().Length > 0)
            {
                return File.Exists(SwarmKeyPath) ? "已选择文件" : "所选文件不存在";
            }

            if (File.Exists(AppPaths.SwarmKeyFile))
            {
                return "使用本机已有的 swarm.key";
            }

            return "必须提供 swarm.key，否则无法加入私有网络";
        }
    }

    public bool SwarmKeyReady =>
        HasEmbeddedSwarmKey ||
        (SwarmKeyPath.Trim().Length > 0 && File.Exists(SwarmKeyPath)) ||
        File.Exists(AppPaths.SwarmKeyFile);

    public string RunAsUser
    {
        get => _runAsUser;
        set => SetProperty(ref _runAsUser, value);
    }

    public bool HardenAcl
    {
        get => _hardenAcl;
        set => SetProperty(ref _hardenAcl, value);
    }

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
            0 => AgentName.Trim().Length > 0 && SwarmKeyReady,
            1 => true,
            _ => false
        };
    }

    private void Next()
    {
        if (Step == 1 && ControllerPeerId.Trim() == "*")
        {
            MessageBox.Show(
                "通配符 \"*\" 会授权私有网络中的所有成员控制本机，仅适用于隔离测试环境，本程序不允许写入。",
                "不允许的授权",
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
            Title = "选择 swarm.key",
            Filter = "swarm.key|swarm.key|所有文件 (*.*)|*.*",
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
                HardenAcl);

            var progress = new Progress<InstallStep>(step => Steps.Add(step));
            var result = await _installer.InstallAsync(options, progress).ConfigureAwait(true);

            Succeeded = result.All(s => s.Success);

            if (Succeeded && peerId.Length > 0 && ControllerNote.Trim().Length > 0)
            {
                var state = _uiState.Load();
                state.PeerNotes[peerId] = ControllerNote.Trim();
                _uiState.Save(state);
            }
        }
        catch (Exception ex)
        {
            Steps.Add(new InstallStep("安装中断", false, ex.Message));
            Succeeded = false;
        }
        finally
        {
            Installing = false;
            Finished = true;
        }
    }
}
