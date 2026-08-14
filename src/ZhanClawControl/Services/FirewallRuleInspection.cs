#nullable disable warnings
using System.Collections.Generic;

namespace ZhanClawControl.Services;

public sealed record FirewallRuleInspection(bool Exists, bool MatchesExpectedDefinition, IReadOnlyList<string> Issues, bool QueryFailed = false, string QueryError = "");
