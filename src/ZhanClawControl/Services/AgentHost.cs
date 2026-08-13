using System.Diagnostics;
using System.IO;
using System.Text;

namespace ZhanClawControl.Services;

/// <summary>
/// 无窗口 Agent 宿主。
///
/// p2p-agent.exe 是 CONSOLE 子系统程序（PE subsystem=3），无论由计划任务直接启动
/// 还是经 cmd 包装，都会弹出黑窗。这里让 ZhanClawControl.exe（WinExe，无控制台）
/// 以 --run-agent 模式充当宿主：CreateNoWindow 拉起 Agent，重定向其 stdout/stderr，
/// 逐行加时间戳写入 logs\agent.log。
///
/// 计划任务执行的就是本模式，因此用户看不到任何窗口，同时日志完整可读。
/// </summary>
public static class AgentHost
{
    public const string Switch = "--run-agent";

    private static readonly object LogLock = new();

    public static bool IsHostMode(IEnumerable<string> args) =>
        args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync(CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
        }
        catch
        {
            // 日志目录不可写时仍要尝试启动 Agent
        }

        RollLogIfNeeded();

        StreamWriter? writer = null;
        try
        {
            writer = new StreamWriter(
                new FileStream(
                    AppPaths.AgentLogFile,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete),
                new UTF8Encoding(false))
            {
                AutoFlush = true
            };
        }
        catch
        {
            // 拿不到日志句柄也不阻断 Agent 启动
        }

        void Log(string channel, string? line)
        {
            if (line is null || writer is null)
            {
                return;
            }

            try
            {
                lock (LogLock)
                {
                    writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {channel} {line}");
                }
            }
            catch
            {
                // 日志写失败不影响 Agent
            }
        }

        if (!File.Exists(AppPaths.AgentExe))
        {
            Log("[host]", $"agent executable not found: {AppPaths.AgentExe}");
            writer?.Dispose();
            return 2;
        }

        if (!File.Exists(AppPaths.ConfigFile))
        {
            Log("[host]", $"config not found: {AppPaths.ConfigFile}");
            writer?.Dispose();
            return 3;
        }

        var psi = new ProcessStartInfo
        {
            FileName = AppPaths.AgentExe,
            // 与官方安装脚本保持一致：工作目录是程序目录，不是数据目录
            WorkingDirectory = AppPaths.InstallRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add("-config");
        psi.ArgumentList.Add(AppPaths.ConfigFile);

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => Log("[agent]", e.Data);
        process.ErrorDataReceived += (_, e) => Log("[agent:err]", e.Data);

        Log("[host]", $"starting \"{AppPaths.AgentExe}\" -config \"{AppPaths.ConfigFile}\"");

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            Log("[host]", $"failed to start agent: {ex.GetType().Name}: {ex.Message}");
            writer?.Dispose();
            return 4;
        }

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            Log("[host]", $"agent exited with code {process.ExitCode}");
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Log("[host]", "host cancelled, terminating agent");
            TryKill(process);
            return 5;
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch
        {
            // 已退出或无权限
        }
    }

    /// <summary>宿主启动时滚动日志，避免与 GUI 侧争抢文件句柄。</summary>
    private static void RollLogIfNeeded()
    {
        try
        {
            if (!File.Exists(AppPaths.AgentLogFile))
            {
                return;
            }

            if (new FileInfo(AppPaths.AgentLogFile).Length <= AppPaths.LogRollThresholdBytes)
            {
                return;
            }

            if (File.Exists(AppPaths.AgentLogRollFile))
            {
                File.Delete(AppPaths.AgentLogRollFile);
            }

            File.Move(AppPaths.AgentLogFile, AppPaths.AgentLogRollFile);
        }
        catch
        {
            // 滚动失败就继续追加
        }
    }
}
