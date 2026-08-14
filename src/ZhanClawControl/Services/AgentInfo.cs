#nullable disable warnings
using System.Collections.Generic;

namespace ZhanClawControl.Services;

public sealed record AgentInfo(string PeerId, string Version, string AgentName, string RelayPeerId, bool? ReservationReady, bool? MdnsReady, int? ConnectedRemoteCount, int? RunningTasks, int? AvailableTaskSlots, IReadOnlyList<string> ListenAddresses, string RawJson);
