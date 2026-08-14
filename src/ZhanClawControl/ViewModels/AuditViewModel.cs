#nullable disable warnings
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using ZhanClawControl.Infrastructure;
using ZhanClawControl.Services;
using ZhanClawControl.Views.Dialogs;

namespace ZhanClawControl.ViewModels;

public sealed class AuditViewModel : ObservableObject
{
	private readonly JournalService _journal = new JournalService();

	private readonly AgentLogService _log = new AgentLogService();

	private readonly UiStateService _uiState = new UiStateService();

	private string _logText = "";

	private string _journalSummary = "";

	private bool _isBusy;

	private string _filterText = "";

	private bool _showParseErrorsOnly;

	private IReadOnlyList<JournalRecord> _allRecords = Array.Empty<JournalRecord>();

	private AgentLogReadResult? _lastLogResult;

	public ObservableCollection<JournalRecord> Records { get; } = new ObservableCollection<JournalRecord>();

	public AsyncRelayCommand RefreshCommand { get; }

	public RelayCommand ExportJournalCommand { get; }

	public RelayCommand ClearLogCommand { get; }

	public RelayCommand OpenDataFolderCommand { get; }

	public AsyncRelayCommand CopyDiagnosticsCommand { get; }

	public AsyncRelayCommand SaveDiagnosticsCommand { get; }

	public string LogText
	{
		get
		{
			return _logText;
		}
		private set
		{
			SetProperty(ref _logText, value, "LogText");
		}
	}

	public string JournalSummary
	{
		get
		{
			return _journalSummary;
		}
		private set
		{
			SetProperty(ref _journalSummary, value, "JournalSummary");
		}
	}

	public string FilterText
	{
		get
		{
			return _filterText;
		}
		set
		{
			if (SetProperty(ref _filterText, value, "FilterText"))
			{
				ApplyFilter();
			}
		}
	}

	public bool ShowParseErrorsOnly
	{
		get
		{
			return _showParseErrorsOnly;
		}
		set
		{
			if (SetProperty(ref _showParseErrorsOnly, value, "ShowParseErrorsOnly"))
			{
				ApplyFilter();
			}
		}
	}

	public bool IsBusy
	{
		get
		{
			return _isBusy;
		}
		private set
		{
			if (SetProperty(ref _isBusy, value, "IsBusy"))
			{
				RefreshCommand.RaiseCanExecuteChanged();
				ExportJournalCommand.RaiseCanExecuteChanged();
				ClearLogCommand.RaiseCanExecuteChanged();
				OpenDataFolderCommand.RaiseCanExecuteChanged();
				CopyDiagnosticsCommand.RaiseCanExecuteChanged();
				SaveDiagnosticsCommand.RaiseCanExecuteChanged();
			}
		}
	}

	public AuditViewModel()
	{
		RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => !IsBusy);
		ExportJournalCommand = new RelayCommand(ExportJournal, () => !IsBusy);
		ClearLogCommand = new RelayCommand(ClearLog, () => !IsBusy);
		OpenDataFolderCommand = new RelayCommand(OpenDataFolder, () => !IsBusy);
		CopyDiagnosticsCommand = new AsyncRelayCommand(CopyDiagnosticsAsync, () => !IsBusy);
		SaveDiagnosticsCommand = new AsyncRelayCommand(SaveDiagnosticsAsync, () => !IsBusy);
	}

	private static string L(string key)
	{
		return App.Localization.Text(key);
	}

	private static string F(string key, params object?[] values)
	{
		return App.Localization.Format(key, values);
	}

	public async Task RefreshAsync(CancellationToken ct = default(CancellationToken))
	{
		if (IsBusy)
		{
			return;
		}
		IsBusy = true;
		try
		{
			_allRecords = await _journal.ReadRecentAsync(300, ct).ConfigureAwait(continueOnCapturedContext: true);
			ApplyFilter();
			UpdateJournalSummary();
			_lastLogResult = await _log.ReadTailResultAsync(500, ct).ConfigureAwait(continueOnCapturedContext: true);
			ApplyLogResult(_lastLogResult);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex2)
		{
			JournalSummary = F("AuditReadFailed", ex2.Message);
		}
		finally
		{
			IsBusy = false;
		}
	}

	private void ApplyLogResult(AgentLogReadResult log)
	{
		LogText = log.Status switch
		{
			AgentLogReadStatus.Success => log.Text, 
			AgentLogReadStatus.Missing => L("AuditLogMissing"), 
			AgentLogReadStatus.Empty => L("AuditLogEmpty"), 
			_ => F("AuditLogFailed", log.ErrorCode), 
		};
	}

	private void UpdateJournalSummary()
	{
		if (_journal.LastReadError != null)
		{
			JournalSummary = F("AuditReadFailed", _journal.LastReadError);
			return;
		}
		if (!_journal.Exists)
		{
			JournalSummary = L("AuditNoRecords");
			return;
		}
		int num = (from r in _allRecords
			select r.SourcePeer into s
			where s.Length > 0
			select s).Distinct<string>(StringComparer.Ordinal).Count();
		int num2 = _allRecords.Count((JournalRecord r) => !r.ParseSucceeded);
		JournalSummary = F("AuditSummary", _allRecords.Count, num, _journal.FileSizeBytes / 1024) + ((num2 > 0) ? (" " + F("AuditParseErrorCount", num2)) : "");
	}

	private void ApplyFilter()
	{
		Records.Clear();
		IEnumerable<JournalRecord> enumerable = _allRecords;
		if (ShowParseErrorsOnly)
		{
			enumerable = enumerable.Where((JournalRecord r) => !r.ParseSucceeded);
		}
		string filter = FilterText.Trim();
		if (filter.Length > 0)
		{
			enumerable = enumerable.Where((JournalRecord r) => r.Action.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.SourcePeer.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.CommandId.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.Status.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.State.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.Error.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.ParseError.Contains(filter, StringComparison.OrdinalIgnoreCase) || (!r.ParseSucceeded && r.Detail.Contains(filter, StringComparison.OrdinalIgnoreCase)));
		}
		foreach (JournalRecord item in enumerable)
		{
			Records.Add(item);
		}
	}

	private void ExportJournal()
	{
		//IL_00de: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_0091: Expected O, but got Unknown
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		if (!_journal.Exists)
		{
			AppDialog.Show(L("AuditNoRecords"), L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)64);
			return;
		}
		SaveFileDialog val = new SaveFileDialog
		{
			Title = L("AuditExport"),
			Filter = L("FileFilterJsonLines"),
			FileName = $"agent-command-journal-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"
		};
		if (AppDialog.ShowFileDialog((CommonDialog)(object)val) != true)
		{
			return;
		}
		try
		{
			_journal.ExportTo(((FileDialog)val).FileName);
		}
		catch (Exception ex)
		{
			AppDialog.Show(F("DialogOperationFailed", ex.Message), L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)16);
		}
	}

	private void ClearLog()
	{
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		if (!(AppDialog.ShowActions("DialogClearLogConfirm", "AuditClearLog", new _003C_003Ez__ReadOnlyArray<AppDialogAction>(new AppDialogAction[2]
		{
			new AppDialogAction("ClearLog", "DialogActionClearLog", AppDialogActionStyle.Danger),
			new AppDialogAction("Cancel", "CommonCancel", AppDialogActionStyle.Secondary, IsDefault: true, IsCancel: true)
		}), (MessageBoxImage)48) != "ClearLog"))
		{
			if (_log.TryClear())
			{
				LogText = L("AuditLogEmpty");
			}
			else
			{
				AppDialog.Show(L("DialogClearLogFailed"), L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)48);
			}
		}
	}

	private static bool ConfirmDiagnostics()
	{
		return AppDialog.ShowActions("DialogDiagnosticsWarning", "ProductName", new _003C_003Ez__ReadOnlyArray<AppDialogAction>(new AppDialogAction[2]
		{
			new AppDialogAction("Continue", "DialogActionContinue", AppDialogActionStyle.Primary),
			new AppDialogAction("Cancel", "CommonCancel", AppDialogActionStyle.Secondary, IsDefault: true, IsCancel: true)
		}), (MessageBoxImage)48) == "Continue";
	}

	private async Task CopyDiagnosticsAsync()
	{
		if (!ConfirmDiagnostics())
		{
			return;
		}
		IsBusy = true;
		try
		{
			Clipboard.SetText(await DiagnosticsCollector.CollectAsync().ConfigureAwait(continueOnCapturedContext: true));
			AppDialog.Show(L("DialogDiagnosticsCopied"), L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)64);
		}
		catch (Exception ex)
		{
			AppDialog.Show(F("DialogCopyFailed", ex.Message), L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)16);
		}
		finally
		{
			IsBusy = false;
		}
	}

	private async Task SaveDiagnosticsAsync()
	{
		if (!ConfirmDiagnostics())
		{
			return;
		}
		SaveFileDialog dialog = new SaveFileDialog
		{
			Title = L("AuditSaveDiagnostics"),
			Filter = L("FileFilterText"),
			FileName = $"zhanclaw-endpoint-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
		};
		if (AppDialog.ShowFileDialog((CommonDialog)(object)dialog) != true)
		{
			return;
		}
		IsBusy = true;
		try
		{
			string contents = await DiagnosticsCollector.CollectAsync().ConfigureAwait(continueOnCapturedContext: true);
			await File.WriteAllTextAsync(((FileDialog)dialog).FileName, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)).ConfigureAwait(continueOnCapturedContext: true);
		}
		catch (Exception ex)
		{
			AppDialog.Show(F("DialogSaveFailed", ex.Message), L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)16);
		}
		finally
		{
			IsBusy = false;
		}
	}

	private void OpenDataFolder()
	{
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = "C:\\ProgramData\\P2PAgent",
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			AppDialog.Show(F("DialogOperationFailed", ex.Message), L("ProductName"), (MessageBoxButton)0, (MessageBoxImage)16);
		}
	}

	public void RefreshLanguage()
	{
		ApplyFilter();
		UpdateJournalSummary();
		if ((object)_lastLogResult != null)
		{
			ApplyLogResult(_lastLogResult);
		}
	}
}
