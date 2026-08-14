#nullable disable warnings
using System;
using System.Globalization;

namespace ZhanClawControl.Services;

public sealed record JournalRecord(DateTime? Timestamp, string CommandId, string SourcePeer, string Action, string State, string Status, string DurationMs, string Error, string Detail)
{
	public bool? Acknowledged { get; init; }

	public bool ParseSucceeded { get; init; } = true;

	public string ParseError { get; init; } = "";

	public string AcknowledgedText
	{
		get
		{
			bool? acknowledged = Acknowledged;
			if (acknowledged.HasValue)
			{
				if (acknowledged == true)
				{
					return "true";
				}
				return "false";
			}
			return "—";
		}
	}

	public string DurationText
	{
		get
		{
			if (DurationMs.Length == 0 || !double.TryParse(DurationMs, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
			{
				return "";
			}
			if (!(result >= 1000.0))
			{
				return result.ToString("0", CultureInfo.CurrentCulture) + " ms";
			}
			return (result / 1000.0).ToString("0.0", CultureInfo.CurrentCulture) + " s";
		}
	}

	public string ShortCommandId
	{
		get
		{
			if (CommandId.Length <= 12)
			{
				return CommandId;
			}
			return CommandId.Substring(0, 12);
		}
	}

	public string ShortSourcePeer
	{
		get
		{
			if (SourcePeer.Length <= 14)
			{
				return SourcePeer;
			}
			string text = SourcePeer.Substring(0, 6);
			string sourcePeer = SourcePeer;
			int length = sourcePeer.Length;
			int num = length - 6;
			return text + "…" + sourcePeer.Substring(num, length - num);
		}
	}

	public string TimestampText => Timestamp?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";
}
