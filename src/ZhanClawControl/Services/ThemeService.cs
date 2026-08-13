using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ZhanClawControl.Services;

public enum AppTheme
{
    Light,
    Dark
}

/// <summary>
/// 读取系统 App 模式并在 WM_SETTINGCHANGE / ImmersiveColorSet 时热切换主题字典。
/// 同时为窗口标题栏应用深色模式（DWMWA_USE_IMMERSIVE_DARK_MODE）。
/// </summary>
public sealed class ThemeService
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

    private static readonly Uri LightUri =
        new("pack://application:,,,/Themes/Light.xaml", UriKind.Absolute);

    private static readonly Uri DarkUri =
        new("pack://application:,,,/Themes/Dark.xaml", UriKind.Absolute);

    private ResourceDictionary? _current;
    private readonly List<Window> _tracked = new();
    private string? _interactiveUserSid;

    public AppTheme CurrentTheme { get; private set; } = AppTheme.Light;

    public event EventHandler? ThemeChanged;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static AppTheme DetectSystemTheme(string? interactiveUserSid = null)
    {
        try
        {
            using var key = string.IsNullOrWhiteSpace(interactiveUserSid)
                ? Registry.CurrentUser.OpenSubKey(PersonalizeKey)
                : Registry.Users.OpenSubKey($@"{interactiveUserSid}\{PersonalizeKey}") ??
                  Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i)
            {
                return i == 0 ? AppTheme.Dark : AppTheme.Light;
            }
        }
        catch
        {
            // 注册表不可读时退回浅色
        }

        return AppTheme.Light;
    }

    public void Initialize(string? interactiveUserSid = null)
    {
        _interactiveUserSid = interactiveUserSid;
        Apply(DetectSystemTheme(_interactiveUserSid), force: true);
    }

    public void Refresh()
    {
        Apply(DetectSystemTheme(_interactiveUserSid), force: false);
    }

    private void Apply(AppTheme theme, bool force)
    {
        if (!force && theme == CurrentTheme && _current is not null)
        {
            return;
        }

        var dict = new ResourceDictionary { Source = theme == AppTheme.Dark ? DarkUri : LightUri };
        var merged = Application.Current.Resources.MergedDictionaries;

        // WPF 在合并字典中按「后加入者优先」的逆序查找。
        // 因此必须替换 App.xaml 中已声明的那个主题字典，
        // 而不是再插入一个 —— 否则切到深色时，仍留在后面的浅色字典会把深色画刷全部覆盖掉。
        _current ??= FindDeclaredThemeDictionary(merged);

        var index = _current is null ? -1 : merged.IndexOf(_current);
        if (index >= 0)
        {
            merged[index] = dict;
        }
        else
        {
            merged.Insert(0, dict);
        }

        _current = dict;
        CurrentTheme = theme;

        foreach (var window in _tracked.ToArray())
        {
            ApplyWindowChrome(window);
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>定位 App.xaml 里声明的 Light/Dark 字典，首次应用主题时接管它。</summary>
    private static ResourceDictionary? FindDeclaredThemeDictionary(
        IList<ResourceDictionary> merged)
    {
        foreach (var dictionary in merged)
        {
            var source = dictionary.Source?.OriginalString;
            if (source is null)
            {
                continue;
            }

            if (source.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                source.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase))
            {
                return dictionary;
            }
        }

        return null;
    }

    /// <summary>注册窗口以接收标题栏深色处理与系统主题变更通知。</summary>
    public void Track(Window window)
    {
        if (_tracked.Contains(window))
        {
            return;
        }

        _tracked.Add(window);
        window.Closed += (_, _) => _tracked.Remove(window);

        if (window.IsLoaded)
        {
            HookWindow(window);
        }
        else
        {
            window.SourceInitialized += (_, _) => HookWindow(window);
        }
    }

    private void HookWindow(Window window)
    {
        ApplyWindowChrome(window);

        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A;
        const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;

        if (msg == WM_DWMCOLORIZATIONCOLORCHANGED)
        {
            Refresh();
        }
        else if (msg == WM_SETTINGCHANGE)
        {
            // Windows 11 emits several section names (and occasionally null) for
            // the same Personalize change. Re-reading the exact interactive-user
            // key is cheap and avoids missing a dark/light transition.
            Refresh();
        }

        return IntPtr.Zero;
    }

    private void ApplyWindowChrome(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var useDark = CurrentTheme == AppTheme.Dark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref useDark, sizeof(int));
            }
        }
        catch
        {
            // 旧版本 Windows 不支持该属性，忽略即可
        }
    }
}
