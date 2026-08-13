using System.Collections.ObjectModel;
using System.Windows.Threading;
using ZhanClawControl.Infrastructure;
using ZhanClawControl.Services;

namespace ZhanClawControl.ViewModels;

public sealed class NavItem
{
    public NavItem(string title, string glyph, object page)
    {
        Title = title;
        Glyph = glyph;
        Page = page;
    }

    public string Title { get; }

    /// <summary>Segoe MDL2 Assets 字形。</summary>
    public string Glyph { get; }

    public object Page { get; }
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ControlApiClient _api = new();
    private readonly DispatcherTimer _timer;
    private NavItem? _selectedNav;
    private bool _refreshing;

    public MainViewModel()
    {
        Status = new StatusViewModel(_api);
        Authorization = new AuthorizationViewModel(_api);
        Audit = new AuditViewModel();
        Settings = new SettingsViewModel();

        NavItems = new ObservableCollection<NavItem>
        {
            new("状态", "\uE968", Status),
            new("授权管理", "\uE72E", Authorization),
            new("任务审计", "\uE9D9", Audit),
            new("设置", "\uE713", Settings)
        };

        _selectedNav = NavItems[0];

        Authorization.AuthorizationChanged += (_, _) => SyncAuthorizedPeers();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
    }

    public ObservableCollection<NavItem> NavItems { get; }

    public StatusViewModel Status { get; }
    public AuthorizationViewModel Authorization { get; }
    public AuditViewModel Audit { get; }
    public SettingsViewModel Settings { get; }

    public NavItem? SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (SetProperty(ref _selectedNav, value))
            {
                OnPropertyChanged(nameof(CurrentPage));

                if (value?.Page == Audit)
                {
                    _ = Audit.RefreshAsync();
                }
            }
        }
    }

    public object? CurrentPage => SelectedNav?.Page;

    public string WindowTitle => AppInfo.ProductName;

    public async Task InitializeAsync()
    {
        Authorization.Load();
        Settings.Load();
        SyncAuthorizedPeers();

        await Status.CheckDeploymentAsync().ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        _timer.Start();
    }

    public async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;

        try
        {
            await Status.RefreshAsync().ConfigureAwait(true);
            await Authorization.RefreshOnlineStateAsync().ConfigureAwait(true);
            SyncAuthorizedPeers();
        }
        catch
        {
            // 轮询失败不打断界面
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void SyncAuthorizedPeers()
    {
        Status.AuthorizedCount = Authorization.Peers.Count;
        Status.AuthorizedPeerIds.Clear();
        foreach (var peer in Authorization.Peers)
        {
            Status.AuthorizedPeerIds.Add(peer.PeerId);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _api.Dispose();
    }
}
