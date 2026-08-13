using System.Globalization;
using System.Windows;
using ZhanClawControl.Services;

namespace ZhanClawControl.Localization;

public sealed record LanguageOption(string Code, string DisplayName);

/// <summary>
/// Small, dependency-free runtime localizer. Strings are published as application
/// resources so DynamicResource bindings update without recreating a window.
/// </summary>
public sealed class LocalizationService
{
    public const string Auto = "Auto";
    public const string SimplifiedChinese = "zh-CN";
    public const string TraditionalChinese = "zh-TW";
    public const string English = "en-US";

    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        Auto, SimplifiedChinese, TraditionalChinese, English
    };

    private readonly UiStateService _state = new();
    private CultureInfo _systemCulture = CultureInfo.CurrentUICulture;

    public string SelectedLanguage { get; private set; } = Auto;
    public string EffectiveLanguage { get; private set; } = SimplifiedChinese;
    public string SystemCultureName => ResolveSystemLanguage(_systemCulture);

    public event EventHandler? LanguageChanged;

    public IReadOnlyList<LanguageOption> GetOptions() =>
    [
        new(Auto, Text("LanguageAuto")),
        new(SimplifiedChinese, "简体中文"),
        new(TraditionalChinese, "繁體中文"),
        new(English, "English")
    ];

    public void Initialize(string? interactiveUserCultureName = null)
    {
        if (!string.IsNullOrWhiteSpace(interactiveUserCultureName))
        {
            try { _systemCulture = CultureInfo.GetCultureInfo(interactiveUserCultureName); }
            catch (CultureNotFoundException) { _systemCulture = CultureInfo.CurrentUICulture; }
        }
        var saved = _state.Load().Language;
        SetLanguage(IsSupported(saved) ? saved : Auto, persist: false, force: true);
    }

    public bool SetLanguage(string? language, bool persist = true, bool force = false)
    {
        var selected = IsSupported(language) ? language! : Auto;
        var effective = selected.Equals(Auto, StringComparison.OrdinalIgnoreCase)
            ? ResolveSystemLanguage(_systemCulture)
            : Normalize(selected);

        if (!force &&
            selected.Equals(SelectedLanguage, StringComparison.OrdinalIgnoreCase) &&
            effective.Equals(EffectiveLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var strings = Strings.For(effective);
        foreach (var pair in strings)
        {
            Application.Current.Resources[pair.Key] = pair.Value;
        }

        SelectedLanguage = selected;
        EffectiveLanguage = effective;

        // UI strings and formatting must switch as one unit. Setting only the
        // default UI culture leaves the dispatcher thread (and Format()) on the
        // previous number/date culture until the next process launch.
        var culture = CultureInfo.GetCultureInfo(effective);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        if (persist)
        {
            var state = _state.Load();
            state.Language = selected;
            if (!_state.Save(state))
            {
                LanguageChanged?.Invoke(this, EventArgs.Empty);
                return false;
            }
        }

        LanguageChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void RefreshSystemLanguage()
    {
        if (SelectedLanguage.Equals(Auto, StringComparison.OrdinalIgnoreCase))
        {
            SetLanguage(Auto, persist: false, force: true);
        }
    }

    public string Text(string key)
    {
        if (Application.Current?.TryFindResource(key) is string value)
        {
            return value;
        }

        var effective = EffectiveLanguage.Length == 0 ? SimplifiedChinese : EffectiveLanguage;
        return Strings.For(effective).TryGetValue(key, out var fallback) ? fallback : key;
    }

    public string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Text(key), args);

    private static bool IsSupported(string? language) =>
        !string.IsNullOrWhiteSpace(language) && Supported.Contains(language);

    private static string Normalize(string language) => language.ToLowerInvariant() switch
    {
        "zh-tw" => TraditionalChinese,
        "en-us" => English,
        _ => SimplifiedChinese
    };

    private static string ResolveSystemLanguage(CultureInfo culture)
    {
        var name = culture.Name;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return name.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase)
                ? TraditionalChinese
                : SimplifiedChinese;
        }

        return English;
    }
}
