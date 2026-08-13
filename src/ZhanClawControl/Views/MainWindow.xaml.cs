using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using ZhanClawControl.Services;
using ZhanClawControl.ViewModels;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ZhanClawControl.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly UiStateService _uiState = new();
    private NotifyIcon? _trayIcon;
    private bool _reallyExit;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;

        _viewModel.Settings.UninstallCompleted += (_, _) =>
        {
            _reallyExit = true;
            Application.Current.Shutdown();
        };
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

            _trayIcon = new NotifyIcon
            {
                Icon = iconStream is not null ? new Icon(iconStream) : SystemIcons.Application,
                Visible = true,
                Text = AppInfo.ProductName
            };

            var menu = new ContextMenuStrip();

            var openItem = new ToolStripMenuItem("打开主窗口");
            openItem.Click += (_, _) => RestoreWindow();
            menu.Items.Add(openItem);

            menu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("退出控制软件");
            exitItem.Click += (_, _) =>
            {
                var confirm = MessageBox.Show(
                    "退出控制软件不会停止后台 Agent。\n\n" +
                    "如需停止接受远端任务，请先在「状态」页停止 Agent。\n\n确定退出？",
                    "退出",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);

                if (confirm == MessageBoxResult.OK)
                {
                    _reallyExit = true;
                    Application.Current.Shutdown();
                }
            };
            menu.Items.Add(exitItem);

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
        var text = $"{AppInfo.ShortName} · {running} · 授权 {auth}";
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
        }
        else
        {
            _reallyExit = true;
            Application.Current.Shutdown();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Dispose();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }
}
