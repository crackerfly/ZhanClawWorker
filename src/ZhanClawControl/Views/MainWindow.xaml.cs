using System.ComponentModel;
using System.Windows;
using ZhanClawControl.Services;
using ZhanClawControl.ViewModels;
using ZhanClawControl.Views.Dialogs;

// WinForms 与 System.Drawing 只在托盘图标处使用。
// 用命名空间别名而不是 using 指令引入，避免 UserControl / Application / MessageBox 等
// 与 WPF 同名类型产生歧义。
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ZhanClawControl.Views;

public partial class MainWindow : Window
{
    // 与 Themes/Controls.xaml 的 AppFontFamily 保持一致
    private const string TrayFontFamily = "Microsoft YaHei UI";

    private readonly MainViewModel _viewModel = new();
    private readonly UiStateService _uiState = new();
    private WinForms.NotifyIcon? _trayIcon;
    private WinForms.ContextMenuStrip? _trayMenu;
    private WinForms.ToolStripMenuItem? _openTrayItem;
    private WinForms.ToolStripMenuItem? _exitTrayItem;

    // 通知区菜单是 WinForms 控件，不参与 WPF 资源继承，
    // 因此在这里显式使用与应用其余部分相同的 Microsoft YaHei UI。
    private Drawing.Font? _trayFont;

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
        App.Theme.ThemeChanged += OnThemeChanged;
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

            _trayMenu = new WinForms.ContextMenuStrip();
            ApplyTrayMenuTheme();
            ApplyTrayMenuFont();

            _openTrayItem = new WinForms.ToolStripMenuItem(App.Localization.Text("CommonOpenMain"));
            _openTrayItem.Click += (_, _) => RestoreWindow();
            _trayMenu.Items.Add(_openTrayItem);

            _trayMenu.Items.Add(new WinForms.ToolStripSeparator());

            _exitTrayItem = new WinForms.ToolStripMenuItem(App.Localization.Text("CommonExitApp"));
            _exitTrayItem.Click += (_, _) =>
            {
                RestoreWindow();
                if (ConfirmApplicationExit())
                {
                    _reallyExit = true;
                    RequestShutdown();
                }
            };
            _trayMenu.Items.Add(_exitTrayItem);

            ApplyTrayMenuTheme();
            ApplyTrayMenuFont();

            _trayIcon.ContextMenuStrip = _trayMenu;
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

        if (!ConfirmApplicationExit())
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
        App.Theme.ThemeChanged -= OnThemeChanged;
        _viewModel.Settings.UninstallCompleted -= OnUninstallCompleted;
        _viewModel.Dispose();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip = null;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _trayMenu?.Dispose();
        _trayMenu = null;
        _trayFont?.Dispose();
        _trayFont = null;

        RequestShutdown();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_openTrayItem is not null) _openTrayItem.Text = App.Localization.Text("CommonOpenMain");
        if (_exitTrayItem is not null) _exitTrayItem.Text = App.Localization.Text("CommonExitApp");
        UpdateTrayText();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyTrayMenuTheme();
        ApplyTrayMenuFont();
    }

    private void ApplyTrayMenuTheme()
    {
        if (_trayMenu is null)
        {
            return;
        }

        var dark = App.Theme.CurrentTheme == AppTheme.Dark;
        _trayMenu.RenderMode = WinForms.ToolStripRenderMode.System;
        _trayMenu.BackColor = dark
            ? Drawing.Color.FromArgb(43, 43, 43)
            : Drawing.Color.FromArgb(255, 255, 255);
        _trayMenu.ForeColor = dark
            ? Drawing.Color.White
            : Drawing.Color.FromArgb(27, 27, 27);
        foreach (WinForms.ToolStripItem item in _trayMenu.Items)
        {
            item.BackColor = _trayMenu.BackColor;
            item.ForeColor = _trayMenu.ForeColor;
        }
        _trayMenu.Invalidate();
    }

    private void ApplyTrayMenuFont()
    {
        if (_trayMenu is null)
        {
            return;
        }

        try
        {
            // 菜单字号沿用系统菜单磅值，只替换字族；字体缺失时保留系统默认。
            var size = WinForms.SystemFonts.MenuFont?.SizeInPoints ?? 9f;
            _trayFont ??= new Drawing.Font(TrayFontFamily, size);
            if (!string.Equals(_trayFont.Name, TrayFontFamily, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _trayMenu.Font = _trayFont;
            foreach (WinForms.ToolStripItem item in _trayMenu.Items)
            {
                item.Font = _trayFont;
            }
        }
        catch
        {
            // 字体不可用时保持系统默认菜单字体，不影响托盘功能。
        }
    }

    private void OnUninstallCompleted(object? sender, EventArgs e)
    {
        _reallyExit = true;
        RequestShutdown();
    }

    private bool ConfirmApplicationExit()
    {
        return AppDialog.ShowActions(
                   _viewModel.HasUnsavedAuthorizationChanges
                       ? "DialogUnsavedAuthorizationAndAgentExit"
                       : "TrayExitConfirm",
                   "DialogExit",
                   [
                       new("exit", _viewModel.HasUnsavedAuthorizationChanges
                           ? "DialogActionDiscardExit"
                           : "DialogActionExit", AppDialogActionStyle.Danger),
                       new("cancel", "CommonCancel", IsDefault: true, IsCancel: true)
                   ],
                   _viewModel.HasUnsavedAuthorizationChanges
                       ? MessageBoxImage.Warning
                       : MessageBoxImage.Question,
                   this) == "exit";
    }
}
