#nullable disable warnings
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ZhanClawControl.Models;

namespace ZhanClawControl.Services;

public sealed class UiStateService
{
	private const int MaxUiStateBytes = 4194304;

	private const int MaxPeerEntries = 1024;

	private const int MaxPeerNoteChars = 256;

	private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
	{
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		TypeInfoResolver = new DefaultJsonTypeInfoResolver()
	};

	public UiState Load()
	{
		if (TryLoad(AppPaths.UiStateFile, out UiState state))
		{
			return state;
		}
		if (!TryLoad(AppPaths.UiStateFile + ".bak", out state))
		{
			return new UiState();
		}
		return state;
	}

	public bool Save(UiState state)
	{
		string error;
		return Save(state, out error);
	}

	public bool Save(UiState state, out string? error)
	{
		string text = null;
		try
		{
			RuntimeSecurityService.ValidateSecureDataRootForWrite();
			RuntimeSecurityService.RejectReparsePoint(AppPaths.UiStateFile);
			RuntimeSecurityService.RejectReparsePoint(AppPaths.UiStateFile + ".bak");
			string value = JsonSerializer.Serialize(ValidateAndNormalize(state), Options);
			text = Path.Combine("C:\\ProgramData\\P2PAgent", $".{Path.GetFileName(AppPaths.UiStateFile)}.{Guid.NewGuid():N}.tmp");
			using (FileStream fileStream = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.WriteThrough))
			{
				using StreamWriter streamWriter = new StreamWriter(fileStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				streamWriter.Write(value);
				streamWriter.Flush();
				fileStream.Flush(flushToDisk: true);
			}
			RuntimeSecurityService.RejectReparsePoint(text);
			RuntimeSecurityService.ValidateSecureDataRootForWrite();
			RuntimeSecurityService.RejectReparsePoint(AppPaths.UiStateFile);
			RuntimeSecurityService.RejectReparsePoint(AppPaths.UiStateFile + ".bak");
			if (File.Exists(AppPaths.UiStateFile))
			{
				File.Replace(text, AppPaths.UiStateFile, AppPaths.UiStateFile + ".bak", ignoreMetadataErrors: true);
			}
			else
			{
				File.Move(text, AppPaths.UiStateFile);
			}
			RuntimeSecurityService.ValidateSecureDataRootForWrite();
			RuntimeSecurityService.RejectReparsePoint(AppPaths.UiStateFile);
			RuntimeSecurityService.RejectReparsePoint(AppPaths.UiStateFile + ".bak");
			error = null;
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
		finally
		{
			if (text != null)
			{
				try
				{
					File.Delete(text);
				}
				catch
				{
				}
			}
		}
	}

	private static bool TryLoad(string path, out UiState state)
	{
		try
		{
			string text = RuntimeSecurityService.ReadProtectedRuntimeTextFile(path, Encoding.UTF8, 4194304);
			if (string.IsNullOrWhiteSpace(text))
			{
				throw new InvalidDataException("UI state file size is invalid.");
			}
			ValidateNoDuplicateJsonProperties(text);
			UiState uiState = JsonSerializer.Deserialize<UiState>(text, Options);
			if (uiState != null)
			{
				state = ValidateAndNormalize(uiState);
				return true;
			}
		}
		catch
		{
		}
		state = new UiState();
		return false;
	}

	private static UiState ValidateAndNormalize(UiState state)
	{
		if (state.PeerNotes == null || state.LastAllowedPeersBackup == null || state.EffectiveAllowedPeers == null || state.Language == null)
		{
			throw new InvalidDataException("UI state contains a null required collection or language.");
		}
		if (state.PeerNotes.Count > 1024 || state.LastAllowedPeersBackup.Count > 1024 || state.EffectiveAllowedPeers.Count > 1024)
		{
			throw new InvalidDataException("UI state contains too many Peer entries.");
		}
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var (text3, text4) in state.PeerNotes)
		{
			if (!AgentConfigService.IsValidPeerId(text3) || text4 == null)
			{
				throw new InvalidDataException("UI state contains an invalid Peer note entry.");
			}
			string text5 = text4.Trim();
			if (text5.Length > 256 || text5.Any(char.IsControl))
			{
				throw new InvalidDataException("UI state contains an invalid Peer note.");
			}
			if (text5.Length > 0)
			{
				dictionary.Add(text3, text5);
			}
		}
		List<string> lastAllowedPeersBackup = ValidatePeerList(state.LastAllowedPeersBackup, "authorization backup");
		List<string> list = ValidatePeerList(state.EffectiveAllowedPeers, "effective authorization");
		if (!state.EffectiveAllowedPeersKnown)
		{
			list.Clear();
		}
		string language = state.Language.Trim().ToLowerInvariant() switch
		{
			"auto" => "Auto", 
			"zh-cn" => "zh-CN", 
			"zh-tw" => "zh-TW", 
			"en-us" => "en-US", 
			_ => throw new InvalidDataException("UI state contains an unsupported language code."), 
		};
		return new UiState
		{
			PeerNotes = dictionary,
			MinimizeToTray = state.MinimizeToTray,
			LastAllowedPeersBackup = lastAllowedPeersBackup,
			Language = language,
			EffectiveAllowedPeers = list,
			EffectiveAllowedPeersKnown = state.EffectiveAllowedPeersKnown,
			AuthorizationPendingRestart = state.AuthorizationPendingRestart,
			ConfigurationPendingRestart = state.ConfigurationPendingRestart
		};
	}

	private static List<string> ValidatePeerList(IEnumerable<string?> values, string field)
	{
		List<string> list = new List<string>();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		foreach (string value in values)
		{
			if (!AgentConfigService.IsValidPeerId(value) || !hashSet.Add(value))
			{
				throw new InvalidDataException("UI state contains an invalid or duplicate " + field + " PeerID.");
			}
			list.Add(value);
		}
		return list;
	}

	private static void ValidateNoDuplicateJsonProperties(string json)
	{
		using JsonDocument jsonDocument = JsonDocument.Parse(json);
		JsonElement rootElement = jsonDocument.RootElement;
		if (rootElement.ValueKind != JsonValueKind.Object)
		{
			throw new InvalidDataException("UI state root must be a JSON object.");
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		foreach (JsonProperty item in rootElement.EnumerateObject())
		{
			if (!hashSet.Add(item.Name))
			{
				throw new InvalidDataException("UI state contains duplicate JSON properties.");
			}
			if (!string.Equals(item.Name, "PeerNotes", StringComparison.Ordinal) || item.Value.ValueKind != JsonValueKind.Object)
			{
				continue;
			}
			HashSet<string> hashSet2 = new HashSet<string>(StringComparer.Ordinal);
			foreach (JsonProperty item2 in item.Value.EnumerateObject())
			{
				if (!hashSet2.Add(item2.Name))
				{
					throw new InvalidDataException("UI state contains duplicate Peer note keys.");
				}
			}
		}
	}
}
