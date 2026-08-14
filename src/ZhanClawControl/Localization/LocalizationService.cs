#nullable disable warnings
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows;
using ZhanClawControl.Models;
using ZhanClawControl.Services;

namespace ZhanClawControl.Localization;

public sealed class LocalizationService
{
	public const string Auto = "Auto";

	public const string SimplifiedChinese = "zh-CN";

	public const string TraditionalChinese = "zh-TW";

	public const string English = "en-US";

	private static readonly HashSet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Auto", "zh-CN", "zh-TW", "en-US" };

	private readonly UiStateService _state = new UiStateService();

	private CultureInfo _systemCulture = CultureInfo.CurrentUICulture;

	public string SelectedLanguage { get; private set; } = "Auto";

	public string EffectiveLanguage { get; private set; } = "zh-CN";

	public string SystemCultureName => ResolveSystemLanguage(_systemCulture);

	public event EventHandler? LanguageChanged;

	public IReadOnlyList<LanguageOption> GetOptions()
	{
		return new _003C_003Ez__ReadOnlyArray<LanguageOption>(new LanguageOption[4]
		{
			new LanguageOption("Auto", Text("LanguageAuto")),
			new LanguageOption("zh-CN", "简体中文"),
			new LanguageOption("zh-TW", "繁體中文"),
			new LanguageOption("en-US", "English")
		});
	}

	public void Initialize(string? interactiveUserCultureName = null)
	{
		if (!string.IsNullOrWhiteSpace(interactiveUserCultureName))
		{
			try
			{
				_systemCulture = CultureInfo.GetCultureInfo(interactiveUserCultureName);
			}
			catch (CultureNotFoundException)
			{
				_systemCulture = CultureInfo.CurrentUICulture;
			}
		}
		string language = _state.Load().Language;
		SetLanguage(IsSupported(language) ? language : "Auto", persist: false, force: true);
	}

	public bool SetLanguage(string? language, bool persist = true, bool force = false)
	{
		string text = (IsSupported(language) ? language : "Auto");
		string text2 = (text.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? ResolveSystemLanguage(_systemCulture) : Normalize(text));
		if (!force && text.Equals(SelectedLanguage, StringComparison.OrdinalIgnoreCase) && text2.Equals(EffectiveLanguage, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		foreach (KeyValuePair<string, string> item in Strings.For(text2))
		{
			Application.Current.Resources[(object)item.Key] = item.Value;
		}
		SelectedLanguage = text;
		EffectiveLanguage = text2;
		CultureInfo cultureInfo = (CultureInfo.DefaultThreadCurrentUICulture = (CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo(text2)));
		Thread.CurrentThread.CurrentCulture = cultureInfo;
		Thread.CurrentThread.CurrentUICulture = cultureInfo;
		if (persist)
		{
			UiState uiState = _state.Load();
			uiState.Language = text;
			if (!_state.Save(uiState))
			{
				this.LanguageChanged?.Invoke(this, EventArgs.Empty);
				return false;
			}
		}
		this.LanguageChanged?.Invoke(this, EventArgs.Empty);
		return true;
	}

	public void RefreshSystemLanguage()
	{
		if (SelectedLanguage.Equals("Auto", StringComparison.OrdinalIgnoreCase))
		{
			SetLanguage("Auto", persist: false, force: true);
		}
	}

	public string Text(string key)
	{
		Application current = Application.Current;
		if (((current != null) ? current.TryFindResource((object)key) : null) is string result)
		{
			return result;
		}
		if (!Strings.For((EffectiveLanguage.Length == 0) ? "zh-CN" : EffectiveLanguage).TryGetValue(key, out string value))
		{
			return key;
		}
		return value;
	}

	public string Format(string key, params object?[] args)
	{
		return string.Format(CultureInfo.CurrentCulture, Text(key), args);
	}

	private static bool IsSupported(string? language)
	{
		if (!string.IsNullOrWhiteSpace(language))
		{
			return Supported.Contains(language);
		}
		return false;
	}

	private static string Normalize(string language)
	{
		string text = language.ToLowerInvariant();
		if (!(text == "zh-tw"))
		{
			if (text == "en-us")
			{
				return "en-US";
			}
			return "zh-CN";
		}
		return "zh-TW";
	}

	private static string ResolveSystemLanguage(CultureInfo culture)
	{
		string name = culture.Name;
		if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
		{
			if (!name.Contains("Hant", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase))
			{
				return "zh-CN";
			}
			return "zh-TW";
		}
		return "en-US";
	}
}
