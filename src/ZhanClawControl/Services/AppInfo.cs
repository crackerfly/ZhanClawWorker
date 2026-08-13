namespace ZhanClawControl.Services;

/// <summary>
/// 产品名称集中定义。界面标题、对话框标题、托盘提示、计划任务描述都引用这里，
/// 改名时只需修改本文件（外加 csproj 的 Product/AssemblyTitle 与 release.yml 的产物文件名）。
/// </summary>
public static class AppInfo
{
    /// <summary>正式名称。</summary>
    public const string ProductName = "战 Claw 被控端";

    /// <summary>空间受限处使用的简称（如托盘提示叠加状态时）。</summary>
    public const string ShortName = "战 Claw 被控端";

    public const string WizardTitle = ProductName + " - 安装";

    /// <summary>侧栏副标题。</summary>
    public const string Subtitle = "P2P 被控端管理器";
}
