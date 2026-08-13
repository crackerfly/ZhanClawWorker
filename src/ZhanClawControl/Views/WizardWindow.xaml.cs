using System.ComponentModel;
using System.Windows;
using ZhanClawControl.Services;
using ZhanClawControl.ViewModels;

namespace ZhanClawControl.Views;

public partial class WizardWindow : Window
{
    private readonly WizardViewModel _viewModel = new();
    private bool _closingByCommand;

    public WizardWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        _viewModel.RequestClose += (_, success) =>
        {
            _closingByCommand = true;
            Completed?.Invoke(this, success);
        };

        Closing += OnClosing;
    }

    /// <summary>参数为 true 表示安装成功，宿主应继续打开主窗口。</summary>
    public event EventHandler<bool>? Completed;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingByCommand)
        {
            return;
        }

        if (_viewModel.Installing)
        {
            MessageBox.Show(
                "安装正在进行中，请等待当前步骤完成。",
                AppInfo.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            e.Cancel = true;
            return;
        }

        if (_viewModel.Succeeded)
        {
            _closingByCommand = true;
            Completed?.Invoke(this, true);
            return;
        }

        var confirm = MessageBox.Show(
            "尚未完成安装。退出后本机不会作为被控端接入网络。\n\n确定退出？",
            "退出安装",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            e.Cancel = true;
            return;
        }

        _closingByCommand = true;
        Completed?.Invoke(this, false);
    }
}
