using System.ComponentModel;
using System.Windows;
using ZhanClawControl.Services;
using ZhanClawControl.ViewModels;

namespace ZhanClawControl.Views;

public partial class WizardWindow : Window
{
    private readonly WizardViewModel _viewModel = new();

    // 关闭意图已确定（点了「完成」，或在 OnClosing 里确认过退出），
    // 用于避免 OnClosing 重复弹确认框。
    private bool _closeDecided;
    private bool _result;

    public WizardWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // 「完成」按钮：此时不在 Closing 流程中，可以安全调用 Close()
        _viewModel.RequestClose += (_, success) =>
        {
            _result = success;
            _closeDecided = true;
            Close();
        };

        Closing += OnClosing;
        Closed += OnClosed;
    }

    /// <summary>
    /// 窗口完全关闭后触发；参数为 true 表示安装成功，宿主应继续打开主窗口。
    /// 必须在 Closed 而非 Closing 中触发 —— 订阅方若在 Closing 期间调用
    /// Close() / Show()，WPF 会抛 "while a Window is closing"。
    /// </summary>
    public event EventHandler<bool>? Completed;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeDecided)
        {
            return;
        }

        if (_viewModel.Installing)
        {
            MessageBox.Show(
                App.Localization.Text("WizardInstallingClose"),
                App.Localization.Text("ProductName"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            e.Cancel = true;
            return;
        }

        if (_viewModel.Succeeded)
        {
            _result = true;
            _closeDecided = true;
            return;
        }

        var confirm = MessageBox.Show(
            App.Localization.Text("WizardExitConfirm"),
            App.Localization.Text("WizardExitTitle"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            e.Cancel = true;
            return;
        }

        // 这里只记录结果并放行关闭，不调用 Close()
        _result = false;
        _closeDecided = true;
    }

    private void OnClosed(object? sender, EventArgs e) => Completed?.Invoke(this, _result);
}
