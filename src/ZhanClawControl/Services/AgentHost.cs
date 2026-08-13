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

        // 日志只能在写句柄打开前滚动。打开后不共享 Delete/Write，避免 GUI 把仍在写入的
        // agent.log 重命名或截断，导致宿主继续向已改名文件写入。
        RollLogIfNeeded();

        StreamWriter? writer = null;
        try
        {
            writer = new StreamWriter(
                new FileStream(
                    AppPaths.AgentLogFile,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read),
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
                // Agent 自身输出已带 Go 风格时间戳（2026/08/13 23:34:01），不再重复加前缀，
                // 只给缺时间戳的行补上，保证每行都可定位到时刻。
                var text = HasLeadingTimestamp(line)
                    ? $"{channel} {line}"
                    : $"{DateTime.Now:yyyy/MM/dd HH:mm:ss} {channel} {line}";

                lock (LogLock)
                {
                    writer.WriteLine(text);
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

        try
        {
            RuntimeSecurityService.ValidateRuntimeSecretsForCurrentUser();
            var config = new AgentConfigService().Load();
            AgentConfigService.ValidateRuntimeBoundary(config);
            RuntimeSecurityService.ValidateSwarmKey(AppPaths.SwarmKeyFile);
            await RuntimeSecurityService.ValidateAgentPayloadAsync(AppPaths.AgentExe, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("[host]", $"runtime security validation failed: {ex.GetType().Name}: {ex.Message}");
            writer?.Dispose();
            return 7;
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
            var agentExitCode = process.ExitCode;
            // Agent 是常驻进程；在宿主未被取消的情况下，即使子进程自报 0，退出本身也属于异常，
            // 必须让任务计划程序的 RestartOnFailure 生效。真实非零退出码保持不变。
            var hostExitCode = agentExitCode == 0 ? 6 : agentExitCode;
            Log(
                "[host]",
                agentExitCode == 0
                    ? "agent exited unexpectedly with code 0; host maps it to code 6 for restart policy"
                    : $"agent exited with code {agentExitCode}");
            return hostExitCode;
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

    /// <summary>识别形如 "2026/08/13 23:34:01" 的行首时间戳。</summary>
    private static bool HasLeadingTimestamp(string line) =>
        line.Length >= 19 &&
        char.IsDigit(line[0]) && char.IsDigit(line[1]) && char.IsDigit(line[2]) && char.IsDigit(line[3]) &&
        line[4] == '/' && line[7] == '/' && line[10] == ' ' && line[13] == ':' && line[16] == ':';

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

    /// <summary>
    /// 仅在宿主打开写句柄前滚动日志。运行期间的滚动被文件共享模式明确禁止。
    /// </summary>
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
