#nullable disable warnings
using System.Collections.Generic;

namespace ZhanClawControl.Services;

public sealed record ScheduledTaskInspection(bool Exists, bool MatchesExpectedDefinition, string RunAsUser, string RawXml, IReadOnlyList<string> Issues, bool QueryFailed = false, string QueryError = "", bool EffectiveEnabled = false, int EffectiveRunLevel = -1, string RawSecurityDescriptor = "");
