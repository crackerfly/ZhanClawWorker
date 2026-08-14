#nullable disable warnings
using System;
using System.Linq;

namespace ZhanClawControl.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
	public bool TimedOut { get; init; }

	public bool TerminationConfirmed { get; init; } = true;

	public bool OutputComplete { get; init; } = true;

	public bool Success
	{
		get
		{
			if (ExitCode == 0)
			{
				return !TimedOut;
			}
			return false;
		}
	}

	public string CombinedOutput => string.Join(Environment.NewLine, new string[2] { StdOut, StdErr }.Where((string s) => !string.IsNullOrWhiteSpace(s))).Trim();
}
