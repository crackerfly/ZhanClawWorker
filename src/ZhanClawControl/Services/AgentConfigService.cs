#nullable disable warnings
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace ZhanClawControl.Services;

public sealed class AgentConfigService
{
	private const int MaxConfigBytes = 1048576;

	private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		TypeInfoResolver = new DefaultJsonTypeInfoResolver()
	};

	public bool Exists
	{
		get
		{
			try
			{
				long length;
				return RuntimeSecurityService.TryGetProtectedRuntimeFileLength(AppPaths.ConfigFile, out length);
			}
			catch
			{
				return true;
			}
		}
	}

	public JsonObject Load()
	{
		string text;
		try
		{
			text = RuntimeSecurityService.ReadProtectedRuntimeTextFile(AppPaths.ConfigFile, Encoding.UTF8, 1048576);
		}
		catch (FileNotFoundException)
		{
			return CreateDefault();
		}
		catch (DirectoryNotFoundException)
		{
			return CreateDefault();
		}
		if (string.IsNullOrWhiteSpace(text))
		{
			throw new InvalidDataException("agent-config.json 为空。");
		}
		return (JsonNode.Parse(text) as JsonObject) ?? throw new InvalidDataException("agent-config.json 根节点必须是 JSON 对象。");
	}

	public void Save(JsonObject config)
	{
		RuntimeSecurityService.ValidateSecureDataRootForWrite();
		ValidateRuntimeBoundary(config);
		string value = config.ToJsonString(WriteOptions);
		string text = Path.Combine("C:\\ProgramData\\P2PAgent", $".agent-config.{Guid.NewGuid():N}.tmp");
		try
		{
			using (FileStream fileStream = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.WriteThrough))
			{
				using StreamWriter streamWriter = new StreamWriter(fileStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				streamWriter.Write(value);
				streamWriter.Flush();
				fileStream.Flush(flushToDisk: true);
			}
			RuntimeSecurityService.ValidateSecureDataRootForWrite();
			RuntimeSecurityService.RejectReparsePoint(text);
			if (File.Exists(AppPaths.ConfigFile))
			{
				RuntimeSecurityService.RejectReparsePoint(AppPaths.ConfigFile);
			}
			File.Move(text, AppPaths.ConfigFile, overwrite: true);
			RuntimeSecurityService.RejectReparsePoint(AppPaths.ConfigFile);
		}
		finally
		{
			try
			{
				if (File.Exists(text))
				{
					File.Delete(text);
				}
			}
			catch
			{
			}
		}
	}

	public static JsonObject CreateDefault()
	{
		JsonArray jsonArray = new JsonArray();
		string[] defaultBootstrapAddrs = AppPaths.DefaultBootstrapAddrs;
		foreach (string value in defaultBootstrapAddrs)
		{
			jsonArray.Add(value);
		}
		JsonArray value2 = new JsonArray { "worker" };
		return new JsonObject
		{
			["agent_name"] = Environment.MachineName,
			["agent_tags"] = value2,
			["bootstrap_addrs"] = jsonArray,
			["swarm_key"] = ToJsonPath(AppPaths.SwarmKeyFile),
			["identity_file"] = ToJsonPath(AppPaths.IdentityFile),
			["rendezvous_group"] = "p2p-agents",
			["api_listen"] = $"{"127.0.0.1"}:{7432}",
			["api_token_file"] = ToJsonPath(AppPaths.ApiTokenFile),
			["command_journal_file"] = ToJsonPath(AppPaths.JournalFile),
			["max_parallel_tasks"] = 4,
			["max_transfer_bytes"] = 8589934592L,
			["allowed_peers"] = new JsonArray()
		};
	}

	public static string ToJsonPath(string windowsPath)
	{
		return windowsPath.Replace('\\', '/');
	}

	public static string GetString(JsonObject config, string key, string fallback = "")
	{
		if (config[key] is JsonValue jsonValue && jsonValue.TryGetValue<string>(out string value) && value != null)
		{
			return value;
		}
		return fallback;
	}

	public static int GetInt(JsonObject config, string key, int fallback)
	{
		try
		{
			return config[key]?.GetValue<int>() ?? fallback;
		}
		catch
		{
			return fallback;
		}
	}

	public static long GetLong(JsonObject config, string key, long fallback)
	{
		try
		{
			return config[key]?.GetValue<long>() ?? fallback;
		}
		catch
		{
			return fallback;
		}
	}

	public static List<string> GetStringArray(JsonObject config, string key)
	{
		List<string> list = new List<string>();
		if (!(config[key] is JsonArray jsonArray))
		{
			return list;
		}
		foreach (JsonNode item in jsonArray)
		{
			if (item is JsonValue jsonValue && jsonValue.TryGetValue<string>(out string value) && !string.IsNullOrWhiteSpace(value))
			{
				list.Add(value.Trim());
			}
		}
		return list;
	}

	public static void SetStringArray(JsonObject config, string key, IEnumerable<string> values)
	{
		if (string.Equals(key, "allowed_peers", StringComparison.Ordinal))
		{
			SetAllowedPeers(config, values);
			return;
		}
		JsonArray jsonArray = new JsonArray();
		foreach (string value in values)
		{
			jsonArray.Add(value);
		}
		config[key] = jsonArray;
	}

	public static void SetAllowedPeers(JsonObject config, IEnumerable<string> values)
	{
		ArgumentNullException.ThrowIfNull(config, "config");
		ArgumentNullException.ThrowIfNull(values, "values");
		JsonArray jsonArray = new JsonArray();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		foreach (string value in values)
		{
			string text = value ?? "";
			if (!string.Equals(text, text.Trim(), StringComparison.Ordinal))
			{
				throw new InvalidDataException("allowed_peers 中的 PeerID 不得包含首尾空白。");
			}
			if (!IsValidPeerId(text))
			{
				throw new InvalidDataException((text == "*") ? "allowed_peers 禁止使用通配符 *。" : ("allowed_peers 包含无效的 libp2p PeerID：" + text));
			}
			if (!hashSet.Add(text))
			{
				throw new InvalidDataException("allowed_peers 包含重复的 PeerID：" + text);
			}
			jsonArray.Add(text);
		}
		config["allowed_peers"] = jsonArray;
	}

	public static void ValidateAllowedPeers(JsonObject config)
	{
		ValidateAllowedPeersCanonical(config);
	}

	public static void ValidateRuntimeBoundary(JsonObject config)
	{
		ValidateAllowedPeersCanonical(config);
		ValidateOperationalSettings(config);
		if (!string.Equals(GetString(config, "api_listen"), $"{"127.0.0.1"}:{7432}", StringComparison.Ordinal))
		{
			throw new InvalidDataException("api_listen 必须精确绑定本机控制端点。");
		}
		ExpectExactConfiguredPath(config, "swarm_key", AppPaths.SwarmKeyFile);
		ExpectExactConfiguredPath(config, "identity_file", AppPaths.IdentityFile);
		ExpectExactConfiguredPath(config, "api_token_file", AppPaths.ApiTokenFile);
		ExpectExactConfiguredPath(config, "command_journal_file", AppPaths.JournalFile);
	}

	public static void ValidateOperationalSettings(JsonObject config)
	{
		ArgumentNullException.ThrowIfNull(config, "config");
		JsonNode jsonNode = config["agent_name"];
		if (jsonNode != null && (!(jsonNode is JsonValue jsonValue) || !jsonValue.TryGetValue<string>(out string value) || string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl)))
		{
			throw new InvalidDataException("agent_name 必须是 1–128 个非控制字符的字符串。");
		}
		ValidateStringArray(config, "agent_tags", 256, (string _) => true);
		ValidateStringArray(config, "bootstrap_addrs", 32, LooksLikeBootstrapMultiaddr);
		JsonNode jsonNode2 = config["rendezvous_group"];
		if (jsonNode2 != null && (!(jsonNode2 is JsonValue jsonValue2) || !jsonValue2.TryGetValue<string>(out string value2) || !IsValidRendezvousGroup(value2)))
		{
			throw new InvalidDataException("rendezvous_group 必须是非空、不含控制字符且 UTF-8 不超过 256 字节的字符串。");
		}
		JsonNode jsonNode3 = config["max_parallel_tasks"];
		bool flag = jsonNode3 != null;
		if (flag)
		{
			int value3 = default(int);
			bool flag2 = !(jsonNode3 is JsonValue jsonValue3) || !jsonValue3.TryGetValue<int>(out value3);
			if (!flag2)
			{
				bool flag3 = ((value3 < 1 || value3 > 64) ? true : false);
				flag2 = flag3;
			}
			flag = flag2;
		}
		if (flag)
		{
			throw new InvalidDataException("max_parallel_tasks 必须是 1–64 的整数。");
		}
		JsonNode jsonNode4 = config["max_transfer_bytes"];
		flag = jsonNode4 != null;
		if (flag)
		{
			long value4 = default(long);
			bool flag2 = !(jsonNode4 is JsonValue jsonValue4) || !jsonValue4.TryGetValue<long>(out value4);
			if (!flag2)
			{
				bool flag3 = ((value4 < 1 || value4 > 1099511627776L) ? true : false);
				flag2 = flag3;
			}
			flag = flag2;
		}
		if (flag)
		{
			throw new InvalidDataException("max_transfer_bytes 必须是 1 字节至 1 TiB 的整数。");
		}
	}

	private static void ValidateStringArray(JsonObject config, string key, int maxCount, Func<string, bool> predicate)
	{
		if (config[key] == null)
		{
			return;
		}
		if (!(config[key] is JsonArray jsonArray) || jsonArray.Count > maxCount)
		{
			throw new InvalidDataException($"{key} 必须是最多 {maxCount} 项的字符串数组。");
		}
		foreach (JsonNode item in jsonArray)
		{
			if (!(item is JsonValue jsonValue) || !jsonValue.TryGetValue<string>(out string value) || value == null || !predicate(value))
			{
				throw new InvalidDataException(key + " 包含无效值。");
			}
		}
	}

	public static bool LooksLikeBootstrapMultiaddr(string? value)
	{
		if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsWhiteSpace) || !value.StartsWith("/", StringComparison.Ordinal))
		{
			return false;
		}
		string[] array = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
		int num = array.Length;
		if ((uint)(num - 6) > 1u)
		{
			return false;
		}
		int result = default(int);
		bool flag = !IsValidBootstrapHost(array[0], array[1]) || !string.Equals(array[2], "tcp", StringComparison.Ordinal) || !int.TryParse(array[3], NumberStyles.None, CultureInfo.InvariantCulture, out result);
		if (!flag)
		{
			bool flag2 = ((result < 1 || result > 65535) ? true : false);
			flag = flag2;
		}
		if (flag)
		{
			return false;
		}
		int num2 = array.Length - 2;
		flag = array.Length == 7;
		if (flag)
		{
			string text = array[4];
			bool flag2 = ((text == "ws" || text == "wss") ? true : false);
			flag = !flag2;
		}
		if (flag)
		{
			return false;
		}
		if (string.Equals(array[num2], "p2p", StringComparison.Ordinal))
		{
			return IsValidPeerId(array[^1]);
		}
		return false;
	}

	public static bool IsValidRendezvousGroup(string? value)
	{
		if (!string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl))
		{
			return Encoding.UTF8.GetByteCount(value) <= 256;
		}
		return false;
	}

	private static bool IsValidBootstrapHost(string protocol, string value)
	{
		if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
		{
			return false;
		}
		if ((protocol == "ip4" || protocol == "ip6") ? true : false)
		{
			if (!IPAddress.TryParse(value, out IPAddress address))
			{
				return false;
			}
			if (!(protocol == "ip4"))
			{
				return address.AddressFamily == AddressFamily.InterNetworkV6;
			}
			return address.AddressFamily == AddressFamily.InterNetwork;
		}
		bool flag;
		switch (protocol)
		{
		case "dns":
		case "dns4":
		case "dns6":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			return Uri.CheckHostName(value) == UriHostNameType.Dns;
		}
		return false;
	}

	private static void ExpectExactConfiguredPath(JsonObject config, string key, string expected)
	{
		string path = GetString(config, key).Replace('/', Path.DirectorySeparatorChar);
		try
		{
			if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException(key + " 必须精确指向受保护的数据目录。");
			}
		}
		catch (Exception ex) when (!(ex is InvalidDataException))
		{
			throw new InvalidDataException(key + " 路径无效。", ex);
		}
	}

	public static bool IsValidPeerId(string? value)
	{
		bool flag = string.IsNullOrWhiteSpace(value) || value == "*";
		if (!flag)
		{
			int length = value.Length;
			bool flag2 = ((length < 20 || length > 128) ? true : false);
			flag = flag2;
		}
		if (flag || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
		{
			return false;
		}
		if (!TryDecodeBase58(value, out byte[] decoded) || decoded.Length < 3)
		{
			return false;
		}
		int offset = 0;
		ulong value3 = default(ulong);
		flag = !TryReadUnsignedVarint(decoded, ref offset, out var value2) || !TryReadUnsignedVarint(decoded, ref offset, out value3);
		if (!flag)
		{
			bool flag2 = ((value3 > 128 || value3 == 0L) ? true : false);
			flag = flag2;
		}
		if (flag || value3 != (ulong)(decoded.Length - offset))
		{
			return false;
		}
		if (value2 <= uint.MaxValue)
		{
			if (value2 == 18)
			{
				return value3 == 32;
			}
			return true;
		}
		return false;
	}

	private static void ValidateAllowedPeersCanonical(JsonObject config)
	{
		if (config["allowed_peers"] == null)
		{
			return;
		}
		JsonArray obj = (config["allowed_peers"] as JsonArray) ?? throw new InvalidDataException("allowed_peers 必须是字符串数组。");
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		foreach (JsonNode item in obj)
		{
			if (!(item is JsonValue jsonValue) || !jsonValue.TryGetValue<string>(out string value) || value == null)
			{
				throw new InvalidDataException("allowed_peers 必须只包含 PeerID 字符串。");
			}
			if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) || !IsValidPeerId(value))
			{
				throw new InvalidDataException("allowed_peers 包含非规范或无效的 libp2p PeerID。");
			}
			if (!hashSet.Add(value))
			{
				throw new InvalidDataException("allowed_peers 包含重复的 PeerID：" + value);
			}
		}
	}

	private static bool TryDecodeBase58(string value, out byte[] decoded)
	{
		List<byte> list = new List<byte> { 0 };
		foreach (char value2 in value)
		{
			int num = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz".IndexOf(value2);
			if (num < 0)
			{
				decoded = Array.Empty<byte>();
				return false;
			}
			int num2 = num;
			for (int j = 0; j < list.Count; j++)
			{
				num2 += list[j] * 58;
				list[j] = (byte)(num2 & 0xFF);
				num2 >>= 8;
			}
			while (num2 > 0)
			{
				list.Add((byte)(num2 & 0xFF));
				num2 >>= 8;
			}
		}
		int num3 = value.TakeWhile((char c) => c == '1').Count();
		int num4 = list.Count - 1;
		while (num4 >= 0 && list[num4] == 0)
		{
			num4--;
		}
		decoded = new byte[num3 + num4 + 1];
		for (int num5 = 0; num5 <= num4; num5++)
		{
			decoded[decoded.Length - 1 - num5] = list[num5];
		}
		return true;
	}

	private static bool TryReadUnsignedVarint(byte[] bytes, ref int offset, out ulong value)
	{
		value = 0uL;
		for (int i = 0; i < 64; i += 7)
		{
			if (offset >= bytes.Length)
			{
				break;
			}
			byte b = bytes[offset++];
			if (i == 63 && b > 1)
			{
				return false;
			}
			value |= (ulong)((long)(b & 0x7F) << i);
			if ((b & 0x80) == 0)
			{
				return true;
			}
		}
		return false;
	}
}
