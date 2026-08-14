#nullable disable warnings
namespace ZhanClawControl.Services;

public sealed record AgentLogReadResult(AgentLogReadStatus Status, string Text, string ErrorCode = "");
