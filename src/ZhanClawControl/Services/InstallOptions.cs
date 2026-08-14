#nullable disable warnings
using System.Collections.Generic;

namespace ZhanClawControl.Services;

public sealed record InstallOptions(string AgentName, IReadOnlyList<string> AgentTags, IReadOnlyList<string> AllowedPeers, IReadOnlyList<string> BootstrapAddrs, string RendezvousGroup, int MaxParallelTasks, long MaxTransferBytes, string RunAsUser, string? SwarmKeySourcePath, bool HardenAcl);
