#nullable disable warnings
namespace ZhanClawControl.Services;

public sealed record InstallStep(string Title, bool Success, string Detail, InstallStepKind Kind = InstallStepKind.Normal)
{
	public bool RequiresDeferredCleanup => Kind == InstallStepKind.DeferredCleanup;
}
