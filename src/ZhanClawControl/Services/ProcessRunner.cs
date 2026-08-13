using System.Diagnostics;
using System.IO;
using System.Text;

namespace ZhanClawControl.Services;

public sealed class ProcessTerminationUnconfirmedException : OperationCanceledException
{
    public string FileName { get; }

    public ProcessTerminationUnconfirmedException(string fileName, CancellationToken cancellationToken)
        : base($"取消后无法确认子进程已终止：{fileName}", innerException: null, cancellationToken)
    {
        FileName = fileName;
    }
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>进程是否因本方法自己的超时预算而被终止。</summary>
    public bool TimedOut { get; init; }

    /// <summary>
    /// 超时后是否确认目标进程已经退出。正常完成时恒为 true。
    /// false 表示终止失败或在收尾预算内没有观察到退出，调用方不得假定副作用已经停止。
    /// </summary>
    public bool TerminationConfirmed { get; init; } = true;

    /// <summary>stdout/stderr 是否已读至 EOF；超时收尾失败时可能为 false。</summary>
    public bool OutputComplete { get; init; } = true;

    public bool Success => ExitCode == 0 && !TimedOut;

    public string CombinedOutput =>
        string.Join(Environment.NewLine,
            new[] { StdOut, StdErr }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
}

public static class ProcessRunner
{
    /// <summary>同步运行一个控制台程序并捕获输出。不经过 shell，参数以数组传入避免拼接注入。</summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        int timeoutMs = 60_000,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = psi };

        process.Start();
        // ReadToEndAsync 明确给出 EOF 完成任务；只等待进程退出并不能保证 DataReceived
        // 事件已经排空，容易丢掉 stdout/stderr 的最后几行。
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = new CancellationTokenSource();
        if (timeoutMs != Timeout.Infinite)
        {
            timeoutCts.CancelAfter(timeoutMs);
        }

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消和本地超时的语义不同：先尽力终止并等待收尾，然后把取消传播给调用方。
            var terminationConfirmed = await TerminateAndWaitAsync(process).ConfigureAwait(false);
            await DrainOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            if (!terminationConfirmed)
            {
                throw new ProcessTerminationUnconfirmedException(fileName, cancellationToken);
            }
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 超时信号和自然退出可能在同一时刻竞争；若已经确认退出，不应误报超时。
            try
            {
                if (process.HasExited)
                {
                    var (completedOut, completedErr, completedOutput) =
                        await DrainOutputAsync(stdoutTask, stderrTask, TimeSpan.FromSeconds(5))
                            .ConfigureAwait(false);
                    return new ProcessResult(process.ExitCode, completedOut, completedErr)
                    {
                        OutputComplete = completedOutput
                    };
                }
            }
            catch
            {
                // 读取退出状态失败时继续走保守的终止路径。
            }

            var terminated = await TerminateAndWaitAsync(process).ConfigureAwait(false);
            var (capturedOut, capturedErr, timeoutOutputComplete) =
                await DrainOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            var timeoutMessage = terminated
                ? $"process_timeout:{fileName}"
                : $"process_timeout_termination_unconfirmed:{fileName}";
            if (!timeoutOutputComplete)
            {
                timeoutMessage += ";redirected_output_incomplete";
            }

            var combinedError = string.Join(
                Environment.NewLine,
                new[] { capturedErr.TrimEnd(), timeoutMessage }
                    .Where(text => !string.IsNullOrWhiteSpace(text)));

            return new ProcessResult(-1, capturedOut, combinedError)
            {
                TimedOut = true,
                TerminationConfirmed = terminated,
                OutputComplete = timeoutOutputComplete
            };
        }

        var (standardOut, standardErr, outputComplete) =
            await DrainOutputAsync(stdoutTask, stderrTask, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return new ProcessResult(
            process.ExitCode,
            standardOut,
            standardErr)
        {
            OutputComplete = outputComplete
        };
    }

    private static async Task<(string StdOut, string StdErr, bool Complete)> DrainOutputAsync(
        Task<string> stdoutTask,
        Task<string> stderrTask,
        TimeSpan? timeout = null)
    {
        var allOutput = Task.WhenAll(stdoutTask, stderrTask);
        try
        {
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                await allOutput.ConfigureAwait(false);
            }
            else
            {
                await allOutput
                    .WaitAsync(timeout ?? TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            }

            return (stdoutTask.Result, stderrTask.Result, true);
        }
        catch (TimeoutException)
        {
            // 不访问未完成 Task.Result，避免阻塞；进程句柄释放后底层读取任务会自行结束。
            return (
                stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "",
                stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "",
                false);
        }
        catch
        {
            // 输出读取错误不应覆盖更重要的退出码/超时状态。
            return (
                stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "",
                stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "",
                false);
        }
    }

    /// <summary>
    /// 终止整棵进程树，并在有界时间内等待进程退出与重定向输出完成。
    /// 返回 false 时调用方必须假定进程仍可能继续产生副作用。
    /// </summary>
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

        using var settleCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(settleCts.Token).ConfigureAwait(false);
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

    public static string SystemPath(string relative) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), relative);
}
