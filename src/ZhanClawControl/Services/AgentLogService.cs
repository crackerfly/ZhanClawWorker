using System.IO;

namespace ZhanClawControl.Services;

/// <summary>
/// Agent 自身没有日志文件；本程序注册的计划任务经 run-agent.cmd 把 stdout/stderr
/// 重定向到 logs\agent.log。这里负责读取尾部与体积滚动。
/// </summary>
public sealed class AgentLogService
{
    public bool Exists => File.Exists(AppPaths.AgentLogFile);

    public long SizeBytes
    {
        get
        {
            try
            {
                return Exists ? new FileInfo(AppPaths.AgentLogFile).Length : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    public async Task<string> ReadTailAsync(int lineCount = 500, CancellationToken ct = default)
    {
        if (!Exists)
        {
            return "尚无日志。Agent 启动后此处会显示运行输出。";
        }

        try
        {
            var lines = await JournalService
                .ReadLastLinesAsync(AppPaths.AgentLogFile, lineCount, ct, 1024 * 1024)
                .ConfigureAwait(false);

            return lines.Count == 0
                ? "日志文件为空。"
                : string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return $"读取日志失败：{ex.Message}";
        }
    }

    /// <summary>超过阈值时把当前日志转存为 .1，只保留一代，避免无限增长。</summary>
    public void RollIfNeeded()
    {
        try
        {
            if (SizeBytes <= AppPaths.LogRollThresholdBytes)
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
            // 文件被 cmd.exe 持有时无法移动，下次再试
        }
    }

    public void Clear()
    {
        try
        {
            if (Exists)
            {
                File.WriteAllText(AppPaths.AgentLogFile, string.Empty);
            }
        }
        catch
        {
            // 被占用时忽略
        }
    }
}
