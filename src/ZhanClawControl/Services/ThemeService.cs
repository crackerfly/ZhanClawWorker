#nullable disable warnings
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace ZhanClawControl.Services;

public sealed class ThemeService
{
	private const string PersonalizeKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize";

	private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

	private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

	private static readonly Uri LightUri = new Uri("pack://application:,,,/Themes/Light.xaml", UriKind.Absolute);

	private static readonly Uri DarkUri = new Uri("pack://application:,,,/Themes/Dark.xaml", UriKind.Absolute);

	private ResourceDictionary? _current;

	private readonly List<Window> _tracked = new List<Window>();

	private string? _interactiveUserSid;

	public AppTheme CurrentTheme { get; private set; }

	public event EventHandler? ThemeChanged;

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

	public static AppTheme DetectSystemTheme(string? interactiveUserSid = null)
	{
		try
		{
			using RegistryKey registryKey = (string.IsNullOrWhiteSpace(interactiveUserSid) ? Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize") : (Registry.Users.OpenSubKey(interactiveUserSid + "\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize") ?? Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize")));
			object obj = registryKey?.GetValue("AppsUseLightTheme");
			if (obj is int)
			{
				return ((int)obj == 0) ? AppTheme.Dark : AppTheme.Light;
			}
		}
		catch
		{
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
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Expected O, but got Unknown
		if (force || theme != CurrentTheme || _current == null)
		{
			ResourceDictionary val = new ResourceDictionary
			{
				Source = ((theme == AppTheme.Dark) ? DarkUri : LightUri)
			};
			Collection<ResourceDictionary> mergedDictionaries = Application.Current.Resources.MergedDictionaries;
			if (_current == null)
			{
				_current = FindDeclaredThemeDictionary(mergedDictionaries);
			}
			int num = ((_current == null) ? (-1) : mergedDictionaries.IndexOf(_current));
			if (num >= 0)
			{
				mergedDictionaries[num] = val;
			}
			else
			{
				mergedDictionaries.Insert(0, val);
			}
			_current = val;
			CurrentTheme = theme;
			Window[] array = _tracked.ToArray();
			foreach (Window window in array)
			{
				ApplyWindowChrome(window);
			}
			this.ThemeChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	private static ResourceDictionary? FindDeclaredThemeDictionary(IList<ResourceDictionary> merged)
	{
		foreach (ResourceDictionary item in merged)
		{
			string text = item.Source?.OriginalString;
			if (text != null && (text.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) || text.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase)))
			{
				return item;
			}
		}
		return null;
	}

	public void Track(Window window)
	{
		if (_tracked.Contains(window))
		{
			return;
		}
		_tracked.Add(window);
		window.Closed += delegate
		{
			_tracked.Remove(window);
		};
		if (((FrameworkElement)window).IsLoaded)
		{
			HookWindow(window);
			return;
		}
		window.SourceInitialized += delegate
		{
			HookWindow(window);
		};
	}

	private void HookWindow(Window window)
	{
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Expected O, but got Unknown
		ApplyWindowChrome(window);
		PresentationSource obj = PresentationSource.FromVisual((Visual)(object)window);
		HwndSource val = (HwndSource)(object)((obj is HwndSource) ? obj : null);
		if (val != null)
		{
			val.AddHook(new HwndSourceHook(WndProc));
		}
	}

	private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		switch (msg)
		{
		case 800:
			Refresh();
			break;
		case 26:
			Refresh();
			break;
		}
		return IntPtr.Zero;
	}

	private void ApplyWindowChrome(Window window)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			nint handle = new WindowInteropHelper(window).Handle;
			if (handle != IntPtr.Zero)
			{
				int value = ((CurrentTheme == AppTheme.Dark) ? 1 : 0);
				if (DwmSetWindowAttribute(handle, 20, ref value, 4) != 0)
				{
					DwmSetWindowAttribute(handle, 19, ref value, 4);
				}
			}
		}
		catch
		{
		}
	}
}
