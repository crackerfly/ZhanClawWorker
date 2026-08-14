#nullable disable warnings
namespace ZhanClawControl.Services;

public sealed record PeerEntry(string PeerId, string Name, string ConnectionPath, string Scope)
{
	public string ShortPeerId
	{
		get
		{
			if (PeerId.Length <= 14)
			{
				return PeerId;
			}
			string text = PeerId.Substring(0, 6);
			string peerId = PeerId;
			int length = peerId.Length;
			int num = length - 6;
			return text + "…" + peerId.Substring(num, length - num);
		}
	}
}
