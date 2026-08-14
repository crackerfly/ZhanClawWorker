#nullable disable warnings
using System;
using System.Threading;

namespace ZhanClawControl.Services;

public sealed class ProcessTerminationUnconfirmedException : OperationCanceledException
{
	public string FileName { get; }

	public ProcessTerminationUnconfirmedException(string fileName, CancellationToken cancellationToken)
		: base("取消后无法确认子进程已终止：" + fileName, null, cancellationToken)
	{
		FileName = fileName;
	}
}
