using System.ComponentModel;
using System.Windows;
using ZhanClawControl.Services;
using ZhanClawControl.ViewModels;

// WinForms 与 System.Drawing 只在托盘图标处使用。
// 用命名空间别名而不是 using 指令引入，避免 UserControl / Application / MessageBox 等
// 与 WPF 同名类型产生歧义。
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ZhanClawControl.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly UiStateService _uiState = new();
    private WinForms.NotifyIcon? _trayIcon;
    private WinForms.ToolStripMenuItem? _openTrayItem;
    private WinForms.ToolStripMenuItem? _exitTrayItem;

    // 用户确认退出程序（而非最小化到托盘）
    private bool _reallyExit;

    // Shutdown 只允许发起一次
    private bool _shutdownInvoked;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;

        _viewModel.Settings.UninstallCompleted += OnUninstallCompleted;
        App.Localization.LanguageChanged += OnLanguageChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetupTrayIcon();
        await _viewModel.InitializeAsync();
        UpdateTrayText();

        _viewModel.Status.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(StatusViewModel.RunningText)
                or nameof(StatusViewModel.AuthorizedCount))
            {
                UpdateTrayText();
            }
        };
    }

    private void SetupTrayIcon()
    {
        try
        {
            var iconStream = Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;

            _trayIcon = new WinForms.NotifyIcon
            {
                Icon = iconStream is not null
                    ? new Drawing.Icon(iconStream)
                    : Drawing.SystemIcons.Application,
                Visible = true,
                Text = App.Localization.Text("ProductName")
            };

            var menu = new WinForms.ContextMenuStrip();

            _openTrayItem = new WinForms.ToolStripMenuItem(App.Localization.Text("CommonOpenMain"));
            _openTrayItem.Click += (_, _) => RestoreWindow();
            menu.Items.Add(_openTrayItem);

            menu.Items.Add(new WinForms.ToolStripSeparator());

            _exitTrayItem = new WinForms.ToolStripMenuItem(App.Localization.Text("CommonExitApp"));
            _exitTrayItem.Click += (_, _) =>
            {
                var confirm = MessageBox.Show(
                    App.Localization.Text("TrayExitConfirm"),
                    App.Localization.Text("DialogExit"),
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);

                if (confirm == MessageBoxResult.OK && ConfirmDiscardAuthorizationDraft())
                {
                    _reallyExit = true;
                    RequestShutdown();
                }
            };
            menu.Items.Add(_exitTrayItem);

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, _) => RestoreWindow();
        }
        catch
        {
            // 托盘不可用时不影响主窗口
        }
    }

    private void UpdateTrayText()
    {
        if (_trayIcon is null)
        {
            return;
        }

        var running = _viewModel.Status.RunningText;
        var auth = _viewModel.Status.AuthorizedCount;

        // NotifyIcon.Text 有 63 字符上限
        var text = App.Localization.Format("TrayText", App.Localization.Text("ShortName"), running, auth);
        _trayIcon.Text = text.Length > 62 ? text[..62] : text;
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    /// <summary>
    /// 统一的退出入口。绝不能在 Closing 事件中调用 ——
    /// Shutdown 会去关闭正在关闭的窗口，WPF 会抛 "while a Window is closing"。
    /// </summary>
    private void RequestShutdown()
    {
        if (_shutdownInvoked)
        {
            return;
        }

        _shutdownInvoked = true;
        Application.Current.Shutdown();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_reallyExit)
        {
            return;
        }

        if (_uiState.Load().MinimizeToTray && _trayIcon is not null)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        if (!ConfirmDiscardAuthorizationDraft())
        {
            e.Cancel = true;
            return;
        }

        // 放行关闭，真正的退出推迟到 OnClosed
        _reallyExit = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        App.Localization.LanguageChanged -= OnLanguageChanged;
        _viewModel.Settings.UninstallCompleted -= OnUninstallCompleted;
        _viewModel.Dispose();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        RequestShutdown();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_openTrayItem is not null) _openTrayItem.Text = App.Localization.Text("CommonOpenMain");
        if (_exitTrayItem is not null) _exitTrayItem.Text = App.Localization.Text("CommonExitApp");
        if (_trayIcon is not null) _trayIcon.Text = App.Localization.Text("ShortName")[..Math.Min(62, App.Localization.Text("ShortName").Length)];
        UpdateTrayText();
    }

    private void OnUninstallCompleted(object? sender, EventArgs e)
    {
        _reallyExit = true;
        RequestShutdown();
    }

    private bool ConfirmDiscardAuthorizationDraft()
    {
        if (!_viewModel.HasUnsavedAuthorizationChanges) return true;
        return MessageBox.Show(
                   App.Localization.Text("DialogUnsavedAuthorizationExit"),
                   App.Localization.Text("DialogExit"),
                   MessageBoxButton.OKCancel,
                   MessageBoxImage.Warning) == MessageBoxResult.OK;
    }
}
