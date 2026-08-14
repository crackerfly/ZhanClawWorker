#nullable disable warnings
using System.Windows.Media;
using ZhanClawControl.Infrastructure;

namespace ZhanClawControl.ViewModels;

public sealed class NavItem : ObservableObject
{
	public string ResourceKey { get; }

	public string Title => App.Localization.Text(ResourceKey);

	public Geometry Primary { get; }

	public Geometry Secondary { get; }

	public object Page { get; }

	public NavItem(string resourceKey, Geometry primary, Geometry secondary, object page)
	{
		ResourceKey = resourceKey;
		Primary = primary;
		Secondary = secondary;
		Page = page;
	}

	public void RefreshLanguage()
	{
		OnPropertyChanged("Title");
	}
}
