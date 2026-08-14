#nullable disable warnings
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZhanClawControl.Services;

public sealed class JournalService
{
	private const string TailTruncatedSentinel = "journal_record_exceeds_4_mib_ui_tail_window";

	public bool Exists
	{
		get
		{
			try
			{
				long length;
				return RuntimeSecurityService.TryGetProtectedRuntimeFileLength(AppPaths.JournalFile, out length);
			}
			catch
			{
				return true;
			}
		}
	}

	public string? LastReadError { get; private set; }

	public long FileSizeBytes
	{
		get
		{
			try
			{
				long length;
				return RuntimeSecurityService.TryGetProtectedRuntimeFileLength(AppPaths.JournalFile, out length) ? length : 0;
			}
			catch
			{
				return 0L;
			}
		}
	}

	public async Task<IReadOnlyList<JournalRecord>> ReadRecentAsync(int maxRecords = 300, CancellationToken ct = default(CancellationToken))
	{
		List<JournalRecord> records = new List<JournalRecord>();
		LastReadError = null;
		try
		{
			foreach (string item in await ReadLastLinesAsync(AppPaths.JournalFile, maxRecords * 2, ct).ConfigureAwait(continueOnCapturedContext: false))
			{
				if (!string.IsNullOrWhiteSpace(item))
				{
					records.Add(ParseLine(item));
				}
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (FileNotFoundException)
		{
			return records;
		}
		catch (DirectoryNotFoundException)
		{
			return records;
		}
		catch (Exception ex4)
		{
			LastReadError = ex4.GetType().Name;
		}
		records.Reverse();
		return records.Take(maxRecords).ToList();
	}

	public void ExportTo(string destinationPath)
	{
		RuntimeSecurityService.CopyProtectedRuntimeFile(AppPaths.JournalFile, destinationPath, overwrite: true);
	}

	private static JournalRecord ParseLine(string line)
	{
		if (string.Equals(line, "journal_record_exceeds_4_mib_ui_tail_window", StringComparison.Ordinal))
		{
			return CreateParseErrorRecord("The record body was not loaded into the UI.", "journal_record_exceeds_4_mib_ui_tail_window");
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(line);
			JsonElement rootElement = jsonDocument.RootElement;
			if (rootElement.ValueKind != JsonValueKind.Object)
			{
				return CreateParseErrorRecord(line, $"journal_root_kind:{rootElement.ValueKind}");
			}
			string text = Pick(rootElement, "command_id", "message_id", "id") ?? "";
			string sourcePeer = Pick(rootElement, "origin", "from", "peer_id", "source") ?? "";
			string text2 = Pick(rootElement, "action", "primitive", "operation") ?? "";
			string state = Pick(rootElement, "state", "kind", "type", "mode") ?? "";
			string text3 = Pick(rootElement, "status", "reason") ?? "";
			string text4 = Pick(rootElement, "duration_ms") ?? "";
			string text5 = Pick(rootElement, "error") ?? "";
			bool? flag = PickBool(rootElement, "acknowledged", "acked", "acknowledged_by_origin");
			DateTime? timestamp = null;
			string text6 = Pick(rootElement, "updated_utc", "timestamp", "sent_at_utc", "captured_at_utc", "modified_utc");
			if (text6 != null && DateTime.TryParse(text6, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var result))
			{
				timestamp = result;
			}
			if (rootElement.TryGetProperty("result", out var value) && value.ValueKind == JsonValueKind.Object)
			{
				if (text3.Length == 0)
				{
					text3 = Pick(value, "status", "reason") ?? "";
				}
				if (text4.Length == 0)
				{
					text4 = Pick(value, "duration_ms") ?? "";
				}
				if (text5.Length == 0)
				{
					text5 = Pick(value, "error") ?? "";
				}
				if (text.Length == 0)
				{
					text = Pick(value, "command_id", "message_id") ?? "";
				}
				if (text2.Length == 0)
				{
					text2 = InferAction(value);
				}
				bool? flag2 = flag;
				if (!flag2.HasValue)
				{
					flag = PickBool(value, "acknowledged", "acked", "acknowledged_by_origin");
				}
				if (!flag.HasValue && value.TryGetProperty("output", out var value2) && value2.ValueKind == JsonValueKind.Object)
				{
					flag = PickBool(value2, "acknowledged", "acked", "acknowledged_by_origin");
				}
			}
			return new JournalRecord(timestamp, text, sourcePeer, text2, state, text3, text4, text5, line)
			{
				Acknowledged = flag
			};
		}
		catch (Exception ex)
		{
			return CreateParseErrorRecord(line, ex.GetType().Name);
		}
	}

	private static JournalRecord CreateParseErrorRecord(string line, string parseError)
	{
		return new JournalRecord(null, "", "", "", "parse_error", "unparsed", "", parseError, line)
		{
			ParseSucceeded = false,
			ParseError = parseError
		};
	}

	private static string InferAction(JsonElement result)
	{
		if (!result.TryGetProperty("output", out var value) || value.ValueKind != JsonValueKind.Object)
		{
			return "";
		}
		if (value.TryGetProperty("collector", out var value2) && value2.ValueKind == JsonValueKind.Object && (value2.TryGetProperty("exit_code", out var value3) || value2.TryGetProperty("shell", out value3) || value2.TryGetProperty("timed_out", out value3)))
		{
			return "process_execute";
		}
		if (value.TryGetProperty("agent", out var value4) && value4.ValueKind == JsonValueKind.Object && value4.TryGetProperty("providers", out value3))
		{
			return "resource_inspect";
		}
		if (value.TryGetProperty("source_uri", out value3) || value.TryGetProperty("destination_uri", out value3))
		{
			return "resource_transfer";
		}
		if (value.TryGetProperty("entries", out value3) || value.TryGetProperty("resource_uri", out value3) || value.TryGetProperty("logical_disks", out value3))
		{
			return "resource_inspect";
		}
		return "";
	}

	private static string? Pick(JsonElement element, params string[] keys)
	{
		foreach (string propertyName in keys)
		{
			if (!element.TryGetProperty(propertyName, out var value))
			{
				continue;
			}
			switch (value.ValueKind)
			{
			case JsonValueKind.String:
			{
				string text = value.GetString();
				if (!string.IsNullOrWhiteSpace(text))
				{
					return text;
				}
				break;
			}
			case JsonValueKind.Number:
			case JsonValueKind.True:
			case JsonValueKind.False:
				return value.ToString();
			}
		}
		return null;
	}

	private static bool? PickBool(JsonElement element, params string[] keys)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			return null;
		}
		foreach (string propertyName in keys)
		{
			if (!element.TryGetProperty(propertyName, out var value))
			{
				continue;
			}
			if (value.ValueKind == JsonValueKind.True)
			{
				return true;
			}
			if (value.ValueKind == JsonValueKind.False)
			{
				return false;
			}
			if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var result))
			{
				return result;
			}
			if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var value2))
			{
				switch (value2)
				{
				case 1:
					return true;
				case 0:
					return false;
				}
			}
		}
		return null;
	}

	internal static async Task<List<string>> ReadLastLinesAsync(string path, int lineCount, CancellationToken ct, int maxTailBytes = 4194304)
	{
		List<string> result;
		await using (FileStream stream = RuntimeSecurityService.OpenProtectedRuntimeFileForRead(path))
		{
			long length = stream.Length;
			int take = (int)Math.Min(maxTailBytes, length);
			long start = length - take;
			bool beginsInsideLine = false;
			if (start > 0)
			{
				stream.Seek(start - 1, SeekOrigin.Begin);
				byte[] previous = new byte[1];
				beginsInsideLine = await stream.ReadAsync(previous.AsMemory(), ct).ConfigureAwait(continueOnCapturedContext: false) == 1 && previous[0] != 10;
			}
			stream.Seek(start, SeekOrigin.Begin);
			byte[] buffer = new byte[take];
			int offset;
			int num;
			for (offset = 0; offset < take; offset += num)
			{
				num = await stream.ReadAsync(buffer.AsMemory(offset, take - offset), ct).ConfigureAwait(continueOnCapturedContext: false);
				if (num <= 0)
				{
					break;
				}
			}
			List<string> list = (from l in Encoding.UTF8.GetString(buffer, 0, offset).Split('\n')
				select l.TrimEnd('\r')).ToList();
			if (beginsInsideLine && list.Count > 0)
			{
				list.RemoveAt(0);
				list.Insert(0, "journal_record_exceeds_4_mib_ui_tail_window");
			}
			result = list.Where((string l) => l.Length > 0).TakeLast(lineCount).ToList();
		}
		return result;
	}
}
