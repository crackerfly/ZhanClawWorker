#nullable disable warnings
#pragma warning disable CS4014
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZhanClawControl.Services;

public static class ProcessRunner
{
	public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, int timeoutMs = 60000, CancellationToken cancellationToken = default(CancellationToken))
	{
		cancellationToken.ThrowIfCancellationRequested();
		ProcessStartInfo processStartInfo = new ProcessStartInfo
		{
			FileName = fileName,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		foreach (string argument in arguments)
		{
			processStartInfo.ArgumentList.Add(argument);
		}
		using Process process = new Process
		{
			StartInfo = processStartInfo
		};
		process.Start();
		Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
		Task<string> stderrTask = process.StandardError.ReadToEndAsync();
		using CancellationTokenSource timeoutCts = new CancellationTokenSource();
		if (timeoutMs != -1)
		{
			timeoutCts.CancelAfter(timeoutMs);
		}
		using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
		try
		{
			await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			bool terminationConfirmed = await TerminateAndWaitAsync(process).ConfigureAwait(continueOnCapturedContext: false);
			await DrainOutputAsync(stdoutTask, stderrTask).ConfigureAwait(continueOnCapturedContext: false);
			if (!terminationConfirmed)
			{
				throw new ProcessTerminationUnconfirmedException(fileName, cancellationToken);
			}
			throw new OperationCanceledException(cancellationToken);
		}
		catch (OperationCanceledException)
		{
			try
			{
				if (process.HasExited)
				{
					var (stdOut, stdErr, outputComplete) = await DrainOutputAsync(stdoutTask, stderrTask, TimeSpan.FromSeconds(5.0)).ConfigureAwait(continueOnCapturedContext: false);
					return new ProcessResult(process.ExitCode, stdOut, stdErr)
					{
						OutputComplete = outputComplete
					};
				}
			}
			catch
			{
			}
			bool terminationConfirmed = await TerminateAndWaitAsync(process).ConfigureAwait(continueOnCapturedContext: false);
			(string, string, bool) obj2 = await DrainOutputAsync(stdoutTask, stderrTask).ConfigureAwait(continueOnCapturedContext: false);
			string item = obj2.Item1;
			string item2 = obj2.Item2;
			bool item3 = obj2.Item3;
			string text = (terminationConfirmed ? ("process_timeout:" + fileName) : ("process_timeout_termination_unconfirmed:" + fileName));
			if (!item3)
			{
				text += ";redirected_output_incomplete";
			}
			string stdErr2 = string.Join(Environment.NewLine, new string[2]
			{
				item2.TrimEnd(),
				text
			}.Where((string value) => !string.IsNullOrWhiteSpace(value)));
			return new ProcessResult(-1, item, stdErr2)
			{
				TimedOut = true,
				TerminationConfirmed = terminationConfirmed,
				OutputComplete = item3
			};
		}
		var (stdOut2, stdErr3, outputComplete2) = await DrainOutputAsync(stdoutTask, stderrTask, TimeSpan.FromSeconds(5.0)).ConfigureAwait(continueOnCapturedContext: false);
		return new ProcessResult(process.ExitCode, stdOut2, stdErr3)
		{
			OutputComplete = outputComplete2
		};
	}

	private static async Task<(string StdOut, string StdErr, bool Complete)> DrainOutputAsync(Task<string> stdoutTask, Task<string> stderrTask, TimeSpan? timeout = null)
	{
		Task<string[]> allOutput = Task.WhenAll<string>(stdoutTask, stderrTask);
		try
		{
			if (!(timeout == Timeout.InfiniteTimeSpan))
			{
				await allOutput.WaitAsync(timeout ?? TimeSpan.FromSeconds(2.0)).ConfigureAwait(continueOnCapturedContext: false);
			}
			else
			{
				await allOutput.ConfigureAwait(continueOnCapturedContext: false);
			}
			return (StdOut: stdoutTask.Result, StdErr: stderrTask.Result, Complete: true);
		}
		catch (TimeoutException)
		{
			allOutput.ContinueWith((Task<string[]> completed) => completed.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
			return (StdOut: stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "", StdErr: stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "", Complete: false);
		}
		catch
		{
			return (StdOut: stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "", StdErr: stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "", Complete: false);
		}
	}

	private static async Task<bool> TerminateAndWaitAsync(Process process)
	{
		try
		{
			if (process.HasExited)
			{
				return true;
			}
			process.Kill(entireProcessTree: true);
		}
		catch
		{
			try
			{
				return process.HasExited;
			}
			catch
			{
				return false;
			}
		}
		using CancellationTokenSource settleCts = new CancellationTokenSource(TimeSpan.FromSeconds(5.0));
		try
		{
			await process.WaitForExitAsync(settleCts.Token).ConfigureAwait(continueOnCapturedContext: false);
			return true;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
		catch
		{
			try
			{
				return process.HasExited;
			}
			catch
			{
				return false;
			}
		}
	}

	public static string SystemPath(string relative)
	{
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), relative);
	}
}
