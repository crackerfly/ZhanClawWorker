#nullable disable warnings
namespace ZhanClawControl.Services;

internal sealed record ScheduledTaskSnapshot(string Xml, bool Enabled, int RunLevel, string SecurityDescriptor);
