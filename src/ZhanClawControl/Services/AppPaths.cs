#nullable disable warnings
using System.IO;

namespace ZhanClawControl.Services;

public static class AppPaths
{
	public const string InstallRoot = "C:\\Program Files\\P2PAgent";

	public const string DataRoot = "C:\\ProgramData\\P2PAgent";

	public const string ScheduledTaskName = "P2P Agent";

	public const string FirewallRuleName = "StarSoftComm ZhanClaw P2P Agent - Private Inbound";

	public const string IdentityProvisioningMarkerContent = "ZhanClawControl identity provisioning v1\n";

	public const string TokenProvisioningMarkerContent = "ZhanClawControl api token provisioning v1\n";

	public const string LegacyTaskMaintenanceEnabledContent = "ZhanClawControl task maintenance v1 enabled\n";

	public const string LegacyTaskMaintenanceDisabledContent = "ZhanClawControl task maintenance v1 disabled\n";

	public const string TaskMaintenanceMutationEnabledContent = "ZhanClawControl task maintenance v2 mutation enabled\n";

	public const string TaskMaintenanceMutationDisabledContent = "ZhanClawControl task maintenance v2 mutation disabled\n";

	public const string TaskMaintenanceValidationReadyEnabledContent = "ZhanClawControl task maintenance v2 validation-ready enabled\n";

	public const string TaskMaintenanceValidationReadyDisabledContent = "ZhanClawControl task maintenance v2 validation-ready disabled\n";

	public const string MaintenanceStartPermitHeader = "ZhanClawControl maintenance start permit v1";

	public const string ApiHost = "127.0.0.1";

	public const int ApiPort = 7432;

	public const long LogRollThresholdBytes = 8388608L;

	public static readonly string[] DefaultBootstrapAddrs = new string[2] { "/ip4/101.133.233.151/tcp/4001/p2p/12D3KooWJjWc44NKy8SrAa6bXTzm8Z9yq1aeeYSTfWy9jrbuZKJE", "/ip4/101.133.233.151/tcp/4002/ws/p2p/12D3KooWJjWc44NKy8SrAa6bXTzm8Z9yq1aeeYSTfWy9jrbuZKJE" };

	public const string DefaultRendezvousGroup = "p2p-agents";

	public const int DefaultMaxParallelTasks = 4;

	public const long DefaultMaxTransferBytes = 8589934592L;

	public const string AgentPayloadResource = "ZhanClawControl.payload.p2p-agent.exe";

	public const string SwarmKeyPayloadResource = "ZhanClawControl.payload.swarm.key";

	public const string PayloadManifestResource = "ZhanClawControl.payload.payload-manifest.json";

	public static string AgentExe => Path.Combine("C:\\Program Files\\P2PAgent", "p2p-agent.exe");

	public static string ControlExe => Path.Combine("C:\\Program Files\\P2PAgent", "ZhanClawControl.exe");

	public static string IdentityProvisioningMarker => Path.Combine("C:\\Program Files\\P2PAgent", ".identity-provisioning-v1");

	public static string TokenProvisioningMarker => Path.Combine("C:\\Program Files\\P2PAgent", ".api-token-provisioning-v1");

	public static string TaskMaintenanceMarker => Path.Combine("C:\\Program Files\\P2PAgent", ".task-maintenance-v1");

	public static string TaskMaintenanceCleanupMarker => Path.Combine("C:\\Program Files\\P2PAgent", ".task-maintenance-cleanup-v1");

	public static string MaintenanceStartPermit => Path.Combine("C:\\Program Files\\P2PAgent", ".maintenance-start-permit-v1");

	public static string UninstallRecoveryRoot => Path.Combine("C:\\Program Files\\P2PAgent", ".uninstall-recovery-v1");

	public static string UninstallRecoveryStageRoot => Path.Combine("C:\\Program Files\\P2PAgent", ".uninstall-recovery-stage-v1");

	public static string UninstallRecoveryCleanupRoot => Path.Combine("C:\\Program Files\\P2PAgent", ".uninstall-recovery-cleanup-v1");

	public static string UninstallRecoveryStateFile => Path.Combine(UninstallRecoveryRoot, "state.json");

	public static string UninstallRecoveryBackupRoot => Path.Combine(UninstallRecoveryRoot, "rollback");

	public static string UninstallRecoveryDataRoot => Path.Combine(UninstallRecoveryRoot, "data");

	public static string LegacyLauncherCmd => Path.Combine("C:\\ProgramData\\P2PAgent", "run-agent.cmd");

	public static string ConfigFile => Path.Combine("C:\\ProgramData\\P2PAgent", "agent-config.json");

	public static string SwarmKeyFile => Path.Combine("C:\\ProgramData\\P2PAgent", "swarm.key");

	public static string IdentityFile => Path.Combine("C:\\ProgramData\\P2PAgent", "agent-identity.key");

	public static string ApiTokenFile => Path.Combine("C:\\ProgramData\\P2PAgent", "agent-api.token");

	public static string JournalFile => Path.Combine("C:\\ProgramData\\P2PAgent", "agent-command-journal.jsonl");

	public static string LogDirectory => Path.Combine("C:\\ProgramData\\P2PAgent", "logs");

	public static string AgentLogFile => Path.Combine(LogDirectory, "agent.log");

	public static string AgentLogRollFile => Path.Combine(LogDirectory, "agent.log.1");

	public static string UiStateFile => Path.Combine("C:\\ProgramData\\P2PAgent", "control-ui-state.json");

	public static string ApiBaseUrl => $"http://{"127.0.0.1"}:{7432}";
}
