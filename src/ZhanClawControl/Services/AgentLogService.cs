using System.IO;

namespace ZhanClawControl.Services;

public enum AgentLogReadStatus
{
    Success,
    Missing,
    Empty,
    Failed
}

public sealed record AgentLogReadResult(
    AgentLogReadStatus Status,
    string Text,
    string ErrorCode = "");

/// <summary>
/// Agent 自身没有日志文件；后台 AgentHost 捕获 stdout/stderr 写入 logs\agent.log。
/// 这里负责只读尾部，并提供仅在宿主未运行时可成功的清空操作。
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

    /// <summary>结构化读取结果，供本地化 UI 区分“缺失、为空、失败”。</summary>
    public async Task<AgentLogReadResult> ReadTailResultAsync(
        int lineCount = 500,
        CancellationToken ct = default)
    {
        if (!Exists)
        {
            return new AgentLogReadResult(AgentLogReadStatus.Missing, "");
        }

        try
        {
            var lines = await JournalService
                .ReadLastLinesAsync(AppPaths.AgentLogFile, lineCount, ct, 1024 * 1024)
                .ConfigureAwait(false);

            return lines.Count == 0
                ? new AgentLogReadResult(AgentLogReadStatus.Empty, "")
                : new AgentLogReadResult(
                    AgentLogReadStatus.Success,
                    string.Join(Environment.NewLine, lines));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AgentLogReadResult(
                AgentLogReadStatus.Failed,
                "",
                ex.GetType().Name);
        }
    }

    /// <summary>
    /// 兼容旧 ViewModel 的文本入口。新代码应使用 <see cref="ReadTailResultAsync"/>，
    /// 再由 LocalizationService 根据 Status 提供界面文案。
    /// </summary>
    public async Task<string> ReadTailAsync(int lineCount = 500, CancellationToken ct = default)
    {
        var result = await ReadTailResultAsync(lineCount, ct).ConfigureAwait(false);
        return result.Status switch
        {
            AgentLogReadStatus.Success => result.Text,
            AgentLogReadStatus.Missing => "log_missing",
            AgentLogReadStatus.Empty => "log_empty",
            _ => $"log_read_failed:{result.ErrorCode}"
        };
    }

    /// <summary>
    /// 兼容旧调用点的无操作方法。活动日志只能由 AgentHost 在打开写句柄前滚动；
    /// 运行期间重命名会让宿主继续写入已改名文件，所以这里明确不再 Move。
    /// </summary>
    public void RollIfNeeded()
    {
        // 故意留空：保持 ViewModel 现有调用签名，同时保证活动日志不被重命名。
    }

    /// <summary>
    /// 只在宿主没有持有写句柄时清空日志。返回 false 表示 Agent 正在写入或文件不可写。
    /// </summary>
    public bool TryClear()
    {
        try
        {
            if (!Exists)
            {
                return true;
            }

            // AgentHost 以 FileShare.Read 打开写句柄，因此运行期间此处会安全失败，
            // 不会在旧 writer 位置下截断文件并制造稀疏/损坏日志。
            using var stream = new FileStream(
                AppPaths.AgentLogFile,
                FileMode.Open,
                FileAccess.Write,
                FileShare.Read);
            stream.SetLength(0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>同步兼容入口；新 UI 应调用 <see cref="TryClear"/> 并向用户显示失败。</summary>
    public void Clear()
    {
        _ = TryClear();
    }
}
