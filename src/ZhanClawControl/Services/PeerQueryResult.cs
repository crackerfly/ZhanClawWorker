#nullable disable warnings
using System.Collections.Generic;

namespace ZhanClawControl.Services;

public sealed record PeerQueryResult(bool Success, string ErrorCode, IReadOnlyList<PeerEntry> Peers);
