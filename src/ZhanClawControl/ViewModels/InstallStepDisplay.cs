#nullable disable warnings
namespace ZhanClawControl.ViewModels;

public sealed record InstallStepDisplay(string Title, bool Success, string Detail, string TechnicalDetail = "", string ErrorCode = "");
