#nullable disable warnings
namespace ZhanClawControl.Services;

public sealed record DeploymentIssue(string ResourceKey, string Detail = "");
