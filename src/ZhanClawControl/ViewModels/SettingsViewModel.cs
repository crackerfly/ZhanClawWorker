using System.Windows;
using ZhanClawControl.Infrastructure;
using ZhanClawControl.Services;

namespace ZhanClawControl.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
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
    private bool _minimizeToTray = true;
    private bool _isBusy;
    private string _installedVersionText = "";

    public SettingsViewModel()
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        ReloadCommand = new RelayCommand(Load, () => !IsBusy);
        UninstallCommand = new AsyncRelayCommand(UninstallAsync, () => !IsBusy);
    }

    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public AsyncRelayCommand UninstallCommand { get; }

    public string AgentName
    {
        get => _agentName;
        set => SetProperty(ref _agentName, value);
    }

    public string AgentTags
    {
        get => _agentTags;
        set => SetProperty(ref _agentTags, value);
    }

    public string BootstrapAddrs
    {
        get => _bootstrapAddrs;
        set => SetProperty(ref _bootstrapAddrs, value);
    }

    public string RendezvousGroup
    {
        get => _rendezvousGroup;
        set => SetProperty(ref _rendezvousGroup, value);
    }

    public int MaxParallelTasks
    {
        get => _maxParallelTasks;
        set => SetProperty(ref _maxParallelTasks, Math.Clamp(value, 1, 64));
    }

    public long MaxTransferMiB
    {
        get => _maxTransferMiB;
        set => SetProperty(ref _maxTransferMiB, Math.Max(1, value));
    }

    public bool AutoStart
    {
        get => _autoStart;
        set => SetProperty(ref _autoStart, value);
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            if (SetProperty(ref _minimizeToTray, value))
            {
                var state = _uiState.Load();
                state.MinimizeToTray = value;
                _uiState.Save(state);
            }
        }
    }

    public string InstalledVersionText
    {
        get => _installedVersionText;
        private set => SetProperty(ref _installedVersionText, value);
    }

    public string DataRootText => AppPaths.DataRoot;
    public string InstallRootText => AppPaths.InstallRoot;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
                ReloadCommand.RaiseCanExecuteChanged();
                UninstallCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public event EventHandler? UninstallCompleted;

    public void Load()
    {
        var config = _config.Load();

        AgentName = AgentConfigService.GetString(config, "agent_name", Environment.MachineName + "-worker");
        AgentTags = string.Join(", ", AgentConfigService.GetStringArray(config, "agent_tags"));
        BootstrapAddrs = string.Join(Environment.NewLine, AgentConfigService.GetStringArray(config, "bootstrap_addrs"));
        RendezvousGroup = AgentConfigService.GetString(config, "rendezvous_group", AppPaths.DefaultRendezvousGroup);
        MaxParallelTasks = AgentConfigService.GetInt(config, "max_parallel_tasks", AppPaths.DefaultMaxParallelTasks);
        MaxTransferMiB = AgentConfigService.GetLong(config, "max_transfer_bytes", AppPaths.DefaultMaxTransferBytes)
                         / 1024 / 1024;

        MinimizeToTray = _uiState.Load().MinimizeToTray;

        try
        {
            InstalledVersionText = System.IO.File.Exists(AppPaths.AgentExe)
                ? System.Diagnostics.FileVersionInfo.GetVersionInfo(AppPaths.AgentExe).FileVersion ?? "未知"
                : "未安装";
        }
        catch
        {
            InstalledVersionText = "未知";
        }

        _ = LoadTaskStateAsync();
    }

    private async Task LoadTaskStateAsync()
    {
        var state = await _task.GetStateAsync().ConfigureAwait(true);
        AutoStart = state is TaskState.Ready or TaskState.Running or TaskState.Unknown;
    }

    private async Task SaveAsync()
    {
        IsBusy = true;

        try
        {
            var config = _config.Load();

            config["agent_name"] = AgentName.Trim();
            AgentConfigService.SetStringArray(config, "agent_tags", SplitList(AgentTags, ','));
            AgentConfigService.SetStringArray(config, "bootstrap_addrs", SplitLines(BootstrapAddrs));
            config["rendezvous_group"] = RendezvousGroup.Trim();
            config["max_parallel_tasks"] = MaxParallelTasks;
            config["max_transfer_bytes"] = MaxTransferMiB * 1024 * 1024;

            _config.Save(config);

            await _task.SetEnabledAsync(AutoStart).ConfigureAwait(true);

            var restart = MessageBox.Show(
                "配置已保存。\n\nAgent 在启动时读取配置，需要重启才会生效。\n\n现在重启？",
                "保存成功",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);

            if (restart == MessageBoxResult.OK)
            {
                await _task.StopAsync().ConfigureAwait(true);
                await Task.Delay(1500).ConfigureAwait(true);
                await _task.StartAsync().ConfigureAwait(true);
                await InstallerService.WaitForReadyAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败：{ex.Message}", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UninstallAsync()
    {
        var confirm = MessageBox.Show(
            "卸载会停止并删除后台任务、删除 p2p-agent.exe。\n\n" +
            "运行数据（设备身份、配置、任务记录）默认保留，重新安装后可继续使用同一 PeerID。\n\n继续？",
            "卸载被控端",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        var removeData = MessageBox.Show(
            "是否同时删除运行数据？\n\n" +
            "选择「是」将删除设备身份（agent-identity.key）。删除后本机会获得新的 PeerID，\n" +
            "所有主控端都需要重新授权。\n\n" +
            "选择「否」保留数据（推荐）。",
            "运行数据",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;

        IsBusy = true;

        try
        {
            var steps = await _installer.UninstallAsync(removeData).ConfigureAwait(true);
            var failed = steps.Where(s => !s.Success).ToList();

            if (failed.Count > 0)
            {
                MessageBox.Show(
                    "卸载过程中有步骤失败：\n\n" +
                    string.Join(Environment.NewLine, failed.Select(s => $"· {s.Title}：{s.Detail}")),
                    "卸载未完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show("已卸载。", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
            }

            UninstallCompleted?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public static List<string> SplitList(string text, char separator) =>
        text.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static List<string> SplitLines(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}
