#nullable disable warnings
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ZhanClawControl.Services;

public sealed class AgentLogService
{
	public bool Exists
	{
		get
		{
			try
			{
				long length;
				return RuntimeSecurityService.TryGetProtectedRuntimeFileLength(AppPaths.AgentLogFile, out length);
			}
			catch
			{
				return true;
			}
		}
	}

	public long SizeBytes
	{
		get
		{
			try
			{
				long length;
				return RuntimeSecurityService.TryGetProtectedRuntimeFileLength(AppPaths.AgentLogFile, out length) ? length : 0;
			}
			catch
			{
				return 0L;
			}
		}
	}

	public async Task<AgentLogReadResult> ReadTailResultAsync(int lineCount = 500, CancellationToken ct = default(CancellationToken))
	{
		try
		{
			List<string> list = await JournalService.ReadLastLinesAsync(AppPaths.AgentLogFile, lineCount, ct, 1048576).ConfigureAwait(continueOnCapturedContext: false);
			return (list.Count == 0) ? new AgentLogReadResult(AgentLogReadStatus.Empty, "") : new AgentLogReadResult(AgentLogReadStatus.Success, string.Join(Environment.NewLine, list));
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (FileNotFoundException)
		{
			return new AgentLogReadResult(AgentLogReadStatus.Missing, "");
		}
		catch (DirectoryNotFoundException)
		{
			return new AgentLogReadResult(AgentLogReadStatus.Missing, "");
		}
		catch (Exception ex4)
		{
			return new AgentLogReadResult(AgentLogReadStatus.Failed, "", ex4.GetType().Name);
		}
	}

	public async Task<string> ReadTailAsync(int lineCount = 500, CancellationToken ct = default(CancellationToken))
	{
		AgentLogReadResult agentLogReadResult = await ReadTailResultAsync(lineCount, ct).ConfigureAwait(continueOnCapturedContext: false);
		return agentLogReadResult.Status switch
		{
			AgentLogReadStatus.Success => agentLogReadResult.Text, 
			AgentLogReadStatus.Missing => "log_missing", 
			AgentLogReadStatus.Empty => "log_empty", 
			_ => "log_read_failed:" + agentLogReadResult.ErrorCode, 
		};
	}

	public void RollIfNeeded()
	{
	}

	public bool TryClear()
	{
		try
		{
			if (!RuntimeSecurityService.TryGetProtectedRuntimeFileLength(AppPaths.AgentLogFile, out var _))
			{
				return true;
			}
			RuntimeSecurityService.TruncateProtectedRuntimeFile(AppPaths.AgentLogFile);
			return true;
		}
		catch
		{
			return false;
		}
	}

	public void Clear()
	{
		TryClear();
	}
}
