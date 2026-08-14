#nullable disable warnings
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Threading;
using ZhanClawControl.Infrastructure;
using ZhanClawControl.Services;
using ZhanClawControl.Views;

namespace ZhanClawControl.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
	private readonly ControlApiClient _api = new ControlApiClient();

	private readonly DispatcherTimer _timer;

	private NavItem? _selectedNav;

	private bool _refreshing;

	private bool _disposed;

	public ObservableCollection<NavItem> NavItems { get; }

	public StatusViewModel Status { get; }

	public AuthorizationViewModel Authorization { get; }

	public AuditViewModel Audit { get; }

	public SettingsViewModel Settings { get; }

	public NavItem? SelectedNav
	{
		get
		{
			return _selectedNav;
		}
		set
		{
			if (SetProperty(ref _selectedNav, value, "SelectedNav"))
			{
				OnPropertyChanged("CurrentPage");
				if (value?.Page == Audit)
				{
					RefreshAudit();
				}
			}
		}
	}

	public object? CurrentPage => SelectedNav?.Page;

	public string WindowTitle => App.Localization.Text("ProductName");

	public string TrayBackgroundStatus => App.Localization.Format("TrayBackgroundStatus", Status.RunningText);

	public bool HasUnsavedAuthorizationChanges => Authorization.HasUnsavedChanges;

	public MainViewModel()
	{
		//IL_0159: Unknown result type (might be due to invalid IL or missing references)
		//IL_015e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0177: Expected O, but got Unknown
		Status = new StatusViewModel(_api);
		Authorization = new AuthorizationViewModel(_api);
		Audit = new AuditViewModel();
		Settings = new SettingsViewModel();
		NavItems = new ObservableCollection<NavItem>
		{
			new NavItem("NavStatus", PhosphorIcons.House, PhosphorIcons.HouseSecondary, Status),
			new NavItem("NavAuthorization", PhosphorIcons.ShieldCheck, PhosphorIcons.ShieldCheckSecondary, Authorization),
			new NavItem("NavAudit", PhosphorIcons.ListChecks, PhosphorIcons.ListChecksSecondary, Audit),
			new NavItem("NavSettings", PhosphorIcons.Gear, PhosphorIcons.GearSecondary, Settings)
		};
		_selectedNav = NavItems[0];
		Authorization.AuthorizationChanged += OnAuthorizationChanged;
		Status.RuntimeRestartVerified += OnRuntimeRestartVerified;
		Status.PropertyChanged += OnStatusPropertyChanged;
		Settings.RuntimeRestartVerified += OnRuntimeRestartVerified;
		App.Localization.LanguageChanged += OnLanguageChanged;
		_timer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(5.0)
		};
		_timer.Tick += OnTimerTick;
	}

	public async Task InitializeAsync()
	{
		Authorization.TryLoad(out string _);
		Status.InitializeEffectiveAuthorization(Authorization.EffectivePeerIds, Authorization.EffectiveStateKnown);
		await Settings.LoadAsync().ConfigureAwait(continueOnCapturedContext: true);
		SyncConfiguredAuthorization();
		await Status.CheckDeploymentAsync().ConfigureAwait(continueOnCapturedContext: true);
		await RefreshAsync().ConfigureAwait(continueOnCapturedContext: true);
		_timer.Start();
	}

	public async Task RefreshAsync()
	{
		if (_refreshing || _disposed)
		{
			return;
		}
		_refreshing = true;
		try
		{
			await Status.RefreshAsync().ConfigureAwait(continueOnCapturedContext: true);
			await Authorization.RefreshOnlineStateAsync().ConfigureAwait(continueOnCapturedContext: true);
			SyncConfiguredAuthorization();
		}
		catch (OperationCanceledException)
		{
		}
		catch
		{
		}
		finally
		{
			_refreshing = false;
		}
	}

	private async void OnTimerTick(object? sender, EventArgs e)
	{
		try
		{
			await RefreshAsync().ConfigureAwait(continueOnCapturedContext: true);
		}
		catch
		{
		}
	}

	private async void RefreshAudit()
	{
		try
		{
			await Audit.RefreshAsync().ConfigureAwait(continueOnCapturedContext: true);
		}
		catch
		{
		}
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

	private void SyncConfiguredAuthorization()
	{
		Status.SetConfiguredAuthorization(Authorization.ConfiguredPeerIds, Authorization.PendingRestart);
	}

	private void OnLanguageChanged(object? sender, EventArgs e)
	{
		foreach (NavItem navItem in NavItems)
		{
			navItem.RefreshLanguage();
		}
		Status.RefreshLanguage();
		Authorization.RefreshLanguage();
		Audit.RefreshLanguage();
		Settings.RefreshLanguage();
		OnPropertyChanged("WindowTitle");
		OnPropertyChanged("TrayBackgroundStatus");
		RefreshLocalizedState();
	}

	private async void RefreshLocalizedState()
	{
		_ = 1;
		try
		{
			await RefreshAsync().ConfigureAwait(continueOnCapturedContext: true);
			if (SelectedNav?.Page == Audit)
			{
				await Audit.RefreshAsync().ConfigureAwait(continueOnCapturedContext: true);
			}
		}
		catch
		{
		}
	}

	private void OnStatusPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == "RunningText")
		{
			OnPropertyChanged("TrayBackgroundStatus");
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
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
}
