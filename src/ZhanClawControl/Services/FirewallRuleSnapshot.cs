#nullable disable warnings
namespace ZhanClawControl.Services;

internal sealed record FirewallRuleSnapshot(string ApplicationName, int Direction, int Action, int Profiles, bool Enabled, int Protocol, bool EdgeTraversal, string LocalAddresses, string RemoteAddresses, string InterfaceTypes, string Grouping, string ServiceName, bool HasSpecificInterfaces);
