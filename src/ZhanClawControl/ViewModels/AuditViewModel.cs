using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using ZhanClawControl.Infrastructure;
using ZhanClawControl.Services;

namespace ZhanClawControl.ViewModels;

public sealed class AuditViewModel : ObservableObject
{
    private readonly JournalService _journal = new();
    private readonly AgentLogService _log = new();
    private readonly UiStateService _uiState = new();

    private string _logText = "";
    private string _journalSummary = "";
    private bool _isBusy;
    private string _filterText = "";

    public AuditViewModel()
    {
        // RefreshAsync 带可选的 CancellationToken 参数，方法组无法隐式转成 Func<Task>
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => !IsBusy);
        ExportJournalCommand = new RelayCommand(ExportJournal);
        ClearLogCommand = new RelayCommand(ClearLog);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        CopyDiagnosticsCommand = new AsyncRelayCommand(CopyDiagnosticsAsync, () => !IsBusy);
        SaveDiagnosticsCommand = new AsyncRelayCommand(SaveDiagnosticsAsync, () => !IsBusy);
    }

    public ObservableCollection<JournalRecord> Records { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand ExportJournalCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public AsyncRelayCommand CopyDiagnosticsCommand { get; }
    public AsyncRelayCommand SaveDiagnosticsCommand { get; }

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    public string JournalSummary
    {
        get => _journalSummary;
        private set => SetProperty(ref _journalSummary, value);
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                CopyDiagnosticsCommand.RaiseCanExecuteChanged();
                SaveDiagnosticsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private IReadOnlyList<JournalRecord> _allRecords = Array.Empty<JournalRecord>();

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        IsBusy = true;

        try
        {
            _allRecords = await _journal.ReadRecentAsync(300, ct).ConfigureAwait(true);
            ApplyFilter();

            var notes = _uiState.Load().PeerNotes;
            var sources = _allRecords
                .Select(r => r.SourcePeer)
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var named = sources
                .Select(s => notes.TryGetValue(s, out var n) && n.Length > 0 ? n : s[..Math.Min(8, s.Length)])
                .ToList();

            JournalSummary = _journal.Exists
                ? $"共 {_allRecords.Count} 条最近记录，来源 {sources.Count} 个" +
                  (named.Count > 0 ? $"（{string.Join("、", named.Take(4))}{(named.Count > 4 ? " 等" : "")}）" : "") +
                  $"；journal 文件 {_journal.FileSizeBytes / 1024} KB"
                : "尚无任务记录。Agent 收到远端 Command 后此处会出现条目。";

            LogText = await _log.ReadTailAsync(500, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            JournalSummary = $"读取失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        Records.Clear();

        var filter = FilterText.Trim();
        var query = filter.Length == 0
            ? _allRecords
            : _allRecords.Where(r =>
                r.Action.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.SourcePeer.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.CommandId.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.Status.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.State.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.Error.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var record in query)
        {
            Records.Add(record);
        }
    }

    private void ExportJournal()
    {
        if (!_journal.Exists)
        {
            MessageBox.Show("当前没有任务记录可导出。", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出任务记录",
            Filter = "JSON Lines (*.jsonl)|*.jsonl|所有文件 (*.*)|*.*",
            FileName = $"agent-command-journal-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.Copy(AppPaths.JournalFile, dialog.FileName, overwrite: true);
            MessageBox.Show("已导出。", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：{ex.Message}", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearLog()
    {
        var confirm = MessageBox.Show(
            "清空 agent.log？\n\n只影响运行日志，不影响任务记录（journal）。",
            "清空日志",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        _log.Clear();
        LogText = "";
    }

    private async Task CopyDiagnosticsAsync()
    {
        IsBusy = true;

        try
        {
            var text = await DiagnosticsCollector.CollectAsync().ConfigureAwait(true);
            Clipboard.SetText(text);

            MessageBox.Show(
                "诊断信息已复制到剪贴板。\n\n" +
                "内容包含环境、文件状态、计划任务、配置、API 响应与日志尾部，\n" +
                "不包含私网密钥、设备私钥或 API Token 的内容。",
                AppInfo.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"采集失败：{ex.Message}", AppInfo.ProductName,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveDiagnosticsAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存诊断信息",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"zhanclaw-worker-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var text = await DiagnosticsCollector.CollectAsync().ConfigureAwait(true);
            await File.WriteAllTextAsync(dialog.FileName, text, new System.Text.UTF8Encoding(true))
                .ConfigureAwait(true);

            MessageBox.Show("已保存。", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败：{ex.Message}", AppInfo.ProductName,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenDataFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.DataRoot,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开目录：{ex.Message}", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
