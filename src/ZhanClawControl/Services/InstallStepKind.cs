#nullable disable warnings
namespace ZhanClawControl.Services;

public enum InstallStepKind
{
	Normal,
	DeferredCleanup,
	InstallationVerified,
	OperationFailure,
	RollbackSucceeded,
	RollbackFailed,
	CleanupWarning,
	NoMutationFailure
}
