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
    private bool _showParseErrorsOnly;
    private IReadOnlyList<JournalRecord> _allRecords = Array.Empty<JournalRecord>();
    private AgentLogReadResult? _lastLogResult;

    public AuditViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => !IsBusy);
        ExportJournalCommand = new RelayCommand(ExportJournal, () => !IsBusy);
        ClearLogCommand = new RelayCommand(ClearLog, () => !IsBusy);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder, () => !IsBusy);
        CopyDiagnosticsCommand = new AsyncRelayCommand(CopyDiagnosticsAsync, () => !IsBusy);
        SaveDiagnosticsCommand = new AsyncRelayCommand(SaveDiagnosticsAsync, () => !IsBusy);
    }

    private static string L(string key) => App.Localization.Text(key);
    private static string F(string key, params object?[] values) => App.Localization.Format(key, values);

    public ObservableCollection<JournalRecord> Records { get; } = new();
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand ExportJournalCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public AsyncRelayCommand CopyDiagnosticsCommand { get; }
    public AsyncRelayCommand SaveDiagnosticsCommand { get; }
    public string LogText { get => _logText; private set => SetProperty(ref _logText, value); }
    public string JournalSummary { get => _journalSummary; private set => SetProperty(ref _journalSummary, value); }

    public string FilterText
    {
        get => _filterText;
        set { if (SetProperty(ref _filterText, value)) ApplyFilter(); }
    }

    public bool ShowParseErrorsOnly
    {
        get => _showParseErrorsOnly;
        set { if (SetProperty(ref _showParseErrorsOnly, value)) ApplyFilter(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RefreshCommand.RaiseCanExecuteChanged();
            ExportJournalCommand.RaiseCanExecuteChanged();
            ClearLogCommand.RaiseCanExecuteChanged();
            OpenDataFolderCommand.RaiseCanExecuteChanged();
            CopyDiagnosticsCommand.RaiseCanExecuteChanged();
            SaveDiagnosticsCommand.RaiseCanExecuteChanged();
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            _allRecords = await _journal.ReadRecentAsync(300, ct).ConfigureAwait(true);
            ApplyFilter();
            UpdateJournalSummary();

            _lastLogResult = await _log.ReadTailResultAsync(500, ct).ConfigureAwait(true);
            ApplyLogResult(_lastLogResult);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            JournalSummary = F("AuditReadFailed", ex.Message);
        }
        finally { IsBusy = false; }
    }

    private void ApplyLogResult(AgentLogReadResult log)
    {
        LogText = log.Status switch
            {
                AgentLogReadStatus.Success => log.Text,
                AgentLogReadStatus.Missing => L("AuditLogMissing"),
                AgentLogReadStatus.Empty => L("AuditLogEmpty"),
                _ => F("AuditLogFailed", log.ErrorCode)
            };
    }

    private void UpdateJournalSummary()
    {
        if (_journal.LastReadError is not null)
        {
            JournalSummary = F("AuditReadFailed", _journal.LastReadError);
            return;
        }
        if (!_journal.Exists)
        {
            JournalSummary = L("AuditNoRecords");
            return;
        }

        var sourceCount = _allRecords.Select(r => r.SourcePeer).Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal).Count();
        var parseErrors = _allRecords.Count(r => !r.ParseSucceeded);
        JournalSummary = F("AuditSummary", _allRecords.Count, sourceCount, _journal.FileSizeBytes / 1024) +
                         (parseErrors > 0 ? " " + F("AuditParseErrorCount", parseErrors) : "");
    }

    private void ApplyFilter()
    {
        Records.Clear();
        IEnumerable<JournalRecord> query = _allRecords;
        if (ShowParseErrorsOnly) query = query.Where(r => !r.ParseSucceeded);
        var filter = FilterText.Trim();
        if (filter.Length > 0)
        {
            query = query.Where(r =>
                r.Action.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.SourcePeer.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.CommandId.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.Status.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.State.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.Error.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.ParseError.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (!r.ParseSucceeded && r.Detail.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        }
        foreach (var record in query) Records.Add(record);
    }

    private void ExportJournal()
    {
        if (!_journal.Exists)
        {
            MessageBox.Show(L("AuditNoRecords"), L("ProductName"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = L("AuditExport"),
            Filter = L("FileFilterJsonLines"),
            FileName = $"agent-command-journal-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.Copy(AppPaths.JournalFile, dialog.FileName, overwrite: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(F("DialogOperationFailed", ex.Message), L("ProductName"), MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ClearLog()
    {
        if (MessageBox.Show(L("DialogClearLogConfirm"), L("AuditClearLog"), MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (_log.TryClear()) LogText = L("AuditLogEmpty");
        else MessageBox.Show(L("DialogClearLogFailed"), L("ProductName"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static bool ConfirmDiagnostics() =>
        MessageBox.Show(L("DialogDiagnosticsWarning"), L("ProductName"), MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;

    private async Task CopyDiagnosticsAsync()
    {
        if (!ConfirmDiagnostics()) return;
        IsBusy = true;
        try
        {
            Clipboard.SetText(await DiagnosticsCollector.CollectAsync().ConfigureAwait(true));
            MessageBox.Show(L("DialogDiagnosticsCopied"), L("ProductName"), MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(F("DialogCopyFailed", ex.Message), L("ProductName"), MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally { IsBusy = false; }
    }

    private async Task SaveDiagnosticsAsync()
    {
        if (!ConfirmDiagnostics()) return;
        var dialog = new SaveFileDialog
        {
            Title = L("AuditSaveDiagnostics"),
            Filter = L("FileFilterText"),
            FileName = $"zhanclaw-endpoint-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };
        if (dialog.ShowDialog() != true) return;
        IsBusy = true;
        try
        {
            var text = await DiagnosticsCollector.CollectAsync().ConfigureAwait(true);
            await File.WriteAllTextAsync(dialog.FileName, text, new System.Text.UTF8Encoding(true)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(F("DialogSaveFailed", ex.Message), L("ProductName"), MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally { IsBusy = false; }
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
            MessageBox.Show(F("DialogOperationFailed", ex.Message), L("ProductName"), MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    public void RefreshLanguage()
    {
        // Reinsert records so WPF reevaluates the culture-sensitive computed
        // TimestampText/DurationText properties without mutating journal data.
        ApplyFilter();
        UpdateJournalSummary();
        if (_lastLogResult is not null) ApplyLogResult(_lastLogResult);
    }
}
