using System.IO;

namespace ZhanClawControl.Services;

/// <summary>
/// 所有固定路径与常量集中在此。与 install-openclaw-integration.ps1 的 Worker 模式保持一致。
/// </summary>
public static class AppPaths
{
    public const string InstallRoot = @"C:\Program Files\P2PAgent";
    public const string DataRoot = @"C:\ProgramData\P2PAgent";
    public const string ScheduledTaskName = "P2P Agent";

    public static string AgentExe => Path.Combine(InstallRoot, "p2p-agent.exe");

    /// <summary>安装到程序目录的控制软件副本；计划任务执行的就是它（--run-agent 宿主模式）。</summary>
    public static string ControlExe => Path.Combine(InstallRoot, "ZhanClawControl.exe");

    /// <summary>早期版本使用的 cmd 启动器，现已废弃，安装时清理。</summary>
    public static string LegacyLauncherCmd => Path.Combine(DataRoot, "run-agent.cmd");
    public static string ConfigFile => Path.Combine(DataRoot, "agent-config.json");
    public static string SwarmKeyFile => Path.Combine(DataRoot, "swarm.key");
    public static string IdentityFile => Path.Combine(DataRoot, "agent-identity.key");
    public static string ApiTokenFile => Path.Combine(DataRoot, "agent-api.token");
    public static string JournalFile => Path.Combine(DataRoot, "agent-command-journal.jsonl");

    public static string LogDirectory => Path.Combine(DataRoot, "logs");
    public static string AgentLogFile => Path.Combine(LogDirectory, "agent.log");
    public static string AgentLogRollFile => Path.Combine(LogDirectory, "agent.log.1");

    /// <summary>本程序自身的元数据（备注名等），不含任何机密。</summary>
    public static string UiStateFile => Path.Combine(DataRoot, "control-ui-state.json");

    public const string ApiHost = "127.0.0.1";
    public const int ApiPort = 7432;
    public static string ApiBaseUrl => $"http://{ApiHost}:{ApiPort}";

    /// <summary>Agent 日志滚动阈值（字节）。</summary>
    public const long LogRollThresholdBytes = 8L * 1024 * 1024;

    /// <summary>
    /// 默认 bootstrap 地址。若服务器地址或 PeerID 变更，只需修改此处。
    /// </summary>
    public static readonly string[] DefaultBootstrapAddrs =
    {
        "/ip4/101.133.233.151/tcp/4001/p2p/12D3KooWJjWc44NKy8SrAa6bXTzm8Z9yq1aeeYSTfWy9jrbuZKJE",
        "/ip4/101.133.233.151/tcp/4002/ws/p2p/12D3KooWJjWc44NKy8SrAa6bXTzm8Z9yq1aeeYSTfWy9jrbuZKJE"
    };

    public const string DefaultRendezvousGroup = "p2p-agents";
    public const int DefaultMaxParallelTasks = 4;
    public const long DefaultMaxTransferBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>嵌入资源名：Agent 主程序。</summary>
    public const string AgentPayloadResource = "ZhanClawControl.payload.p2p-agent.exe";

    /// <summary>嵌入资源名：可选的 swarm.key。</summary>
    public const string SwarmKeyPayloadResource = "ZhanClawControl.payload.swarm.key";
}
