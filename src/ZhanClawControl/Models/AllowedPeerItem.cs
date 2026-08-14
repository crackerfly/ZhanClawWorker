#nullable disable warnings
using ZhanClawControl.Infrastructure;
using ZhanClawControl.Services;

namespace ZhanClawControl.Models;

public sealed class AllowedPeerItem : ObservableObject
{
	private string _peerId = "";

	private string _note = "";

	private bool? _online;

	public string PeerId
	{
		get
		{
			return _peerId;
		}
		set
		{
			if (SetProperty(ref _peerId, value, "PeerId"))
			{
				OnPropertyChanged("ShortPeerId");
			}
		}
	}

	public string Note
	{
		get
		{
			return _note;
		}
		set
		{
			SetProperty(ref _note, value, "Note");
		}
	}

	public bool? Online
	{
		get
		{
			return _online;
		}
		set
		{
			if (SetProperty(ref _online, value, "Online"))
			{
				OnPropertyChanged("OnlineText");
			}
		}
	}

	public string OnlineText
	{
		get
		{
			bool? online = Online;
			if (online.HasValue)
			{
				if (online == true)
				{
					return App.Localization.Text("AuthorizationOnline");
				}
				return App.Localization.Text("AuthorizationOffline");
			}
			return App.Localization.Text("AuthorizationUnknown");
		}
	}

	public string ShortPeerId
	{
		get
		{
			if (PeerId.Length <= 20)
			{
				return PeerId;
			}
			string text = PeerId.Substring(0, 10);
			string peerId = PeerId;
			int length = peerId.Length;
			int num = length - 8;
			return text + "…" + peerId.Substring(num, length - num);
		}
	}

	public void RefreshLanguage()
	{
		OnPropertyChanged("OnlineText");
	}

	public static bool LooksLikePeerId(string value)
	{
		return AgentConfigService.IsValidPeerId(value);
	}
}

