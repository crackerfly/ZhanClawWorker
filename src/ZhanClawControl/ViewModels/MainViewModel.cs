using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using ZhanClawControl.Infrastructure;
using ZhanClawControl.Services;
using ZhanClawControl.Views;

namespace ZhanClawControl.ViewModels;

public sealed class NavItem : ObservableObject
{
    public NavItem(string resourceKey, Geometry primary, Geometry secondary, object page)
    {
        ResourceKey = resourceKey;
        Primary = primary;
        Secondary = secondary;
        Page = page;
    }

    public string ResourceKey { get; }
    public string Title => App.Localization.Text(ResourceKey);
    public Geometry Primary { get; }
    public Geometry Secondary { get; }
    public object Page { get; }
    public void RefreshLanguage() => OnPropertyChanged(nameof(Title));
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ControlApiClient _api = new();
    private readonly DispatcherTimer _timer;
    private NavItem? _selectedNav;
    private bool _refreshing;
    private bool _disposed;

    public MainViewModel()
    {
        Status = new StatusViewModel(_api);
        Authorization = new AuthorizationViewModel(_api);
        Audit = new AuditViewModel();
        Settings = new SettingsViewModel();
        NavItems = new ObservableCollection<NavItem>
        {
            new("NavStatus", PhosphorIcons.House, PhosphorIcons.HouseSecondary, Status),
            new("NavAuthorization", PhosphorIcons.ShieldCheck, PhosphorIcons.ShieldCheckSecondary, Authorization),
            new("NavAudit", PhosphorIcons.ListChecks, PhosphorIcons.ListChecksSecondary, Audit),
            new("NavSettings", PhosphorIcons.Gear, PhosphorIcons.GearSecondary, Settings)
        };
        _selectedNav = NavItems[0];

        Authorization.AuthorizationChanged += OnAuthorizationChanged;
        Status.RuntimeRestartVerified += OnRuntimeRestartVerified;
        Status.PropertyChanged += OnStatusPropertyChanged;
        Settings.RuntimeRestartVerified += OnRuntimeRestartVerified;
        App.Localization.LanguageChanged += OnLanguageChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += OnTimerTick;
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
            if (!SetProperty(ref _selectedNav, value)) return;
            OnPropertyChanged(nameof(CurrentPage));
            if (value?.Page == Audit) RefreshAudit();
        }
    }

    public object? CurrentPage => SelectedNav?.Page;
    public string WindowTitle => App.Localization.Text("ProductName");
    public string TrayBackgroundStatus => App.Localization.Format("TrayBackgroundStatus", Status.RunningText);
    public bool HasUnsavedAuthorizationChanges => Authorization.HasUnsavedChanges;

    public async Task InitializeAsync()
    {
        Authorization.Load();
        Status.InitializeEffectiveAuthorization(
            Authorization.EffectivePeerIds,
            Authorization.EffectiveStateKnown);
        await Settings.LoadAsync().ConfigureAwait(true);
        SyncConfiguredAuthorization();
        await Status.CheckDeploymentAsync().ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        _timer.Start();
    }

    public async Task RefreshAsync()
    {
        if (_refreshing || _disposed) return;
        _refreshing = true;
        try
        {
            await Status.RefreshAsync().ConfigureAwait(true);
            await Authorization.RefreshOnlineStateAsync().ConfigureAwait(true);
            SyncConfiguredAuthorization();
        }
        catch (OperationCanceledException)
        {
            // Shutdown can cancel an in-flight refresh.
        }
        catch
        {
            // Page-level state represents polling failures; keep the dispatcher alive.
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        try { await RefreshAsync().ConfigureAwait(true); }
        catch { /* RefreshAsync already maps expected failures. */ }
    }

    private async void RefreshAudit()
    {
        try { await Audit.RefreshAsync().ConfigureAwait(true); }
        catch { /* The audit page shows its own read status. */ }
    }

    private void OnAuthorizationChanged(object? sender, AuthorizationChangedEventArgs e)
    {
        SyncConfiguredAuthorization();
        if (e.RuntimeVerified)
        {
            Status.MarkAuthorizationEffective(Authorization.ConfiguredPeerIds);
            Settings.MarkRuntimeApplied();
        }
    }

    private void OnRuntimeRestartVerified(object? sender, EventArgs e)
    {
        if (Authorization.PendingRestart || !Authorization.EffectiveStateKnown)
        {
            Authorization.MarkRuntimeApplied();
            Status.MarkAuthorizationEffective(Authorization.ConfiguredPeerIds);
        }
        Settings.MarkRuntimeApplied();
    }

    private void SyncConfiguredAuthorization() =>
        Status.SetConfiguredAuthorization(
            Authorization.ConfiguredPeerIds,
            Authorization.PendingRestart);

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var item in NavItems) item.RefreshLanguage();
        Status.RefreshLanguage();
        Authorization.RefreshLanguage();
        Audit.RefreshLanguage();
        Settings.RefreshLanguage();
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(TrayBackgroundStatus));
        RefreshLocalizedState();
    }

    private async void RefreshLocalizedState()
    {
        try
        {
            await RefreshAsync().ConfigureAwait(true);
            if (SelectedNav?.Page == Audit) await Audit.RefreshAsync().ConfigureAwait(true);
        }
        catch { /* Language switching must not terminate the dispatcher. */ }
    }

    private void OnStatusPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StatusViewModel.RunningText))
            OnPropertyChanged(nameof(TrayBackgroundStatus));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        Authorization.AuthorizationChanged -= OnAuthorizationChanged;
        Status.RuntimeRestartVerified -= OnRuntimeRestartVerified;
        Status.PropertyChanged -= OnStatusPropertyChanged;
        Settings.RuntimeRestartVerified -= OnRuntimeRestartVerified;
        App.Localization.LanguageChanged -= OnLanguageChanged;
        _api.Dispose();
    }
}
