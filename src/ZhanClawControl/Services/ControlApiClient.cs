#nullable disable warnings
#pragma warning disable CS0649
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace ZhanClawControl.Services;

public sealed class ControlApiClient : IDisposable
{
	private enum TcpTableClass
	{
		OwnerPidListener = 3,
		OwnerPidAll = 5
	}

	private struct MibTcpRowOwnerPid
	{
		public uint State;

		public uint LocalAddress;

		public uint LocalPort;

		public uint RemoteAddress;

		public uint RemotePort;

		public uint OwningPid;
	}

	private const int MaxResponseBytes = 16777216;

	private const int MaxApiTokenBytes = 65536;

	private const int AfInet = 2;

	private const uint TcpStateListen = 2u;

	private const uint TcpStateEstablished = 5u;

	private const uint ErrorInsufficientBuffer = 122u;

	private const uint NoError = 0u;

	private static readonly HttpRequestOptionsKey<int> ExpectedApiProcessId = new HttpRequestOptionsKey<int>("ZhanClawControl.ExpectedApiProcessId");

	private static readonly JsonSerializerOptions PrettyOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		TypeInfoResolver = new DefaultJsonTypeInfoResolver()
	};

	private readonly HttpClient _http;

	public ControlApiClient()
	{
		SocketsHttpHandler handler = new SocketsHttpHandler
		{
			UseProxy = false,
			AllowAutoRedirect = false,
			UseCookies = false,
			AutomaticDecompression = DecompressionMethods.None,
			PooledConnectionLifetime = TimeSpan.Zero,
			PooledConnectionIdleTimeout = TimeSpan.Zero,
			ConnectCallback = ConnectTrustedApiAsync
		};
		_http = new HttpClient(handler, disposeHandler: true)
		{
			BaseAddress = new Uri(AppPaths.ApiBaseUrl),
			Timeout = TimeSpan.FromSeconds(10.0),
			DefaultRequestVersion = HttpVersion.Version11,
			DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
		};
	}

	public static async Task<bool> IsPortOpenAsync(int timeoutMs = 500, CancellationToken ct = default(CancellationToken))
	{
		ct.ThrowIfCancellationRequested();
		using TcpClient client = new TcpClient();
		using CancellationTokenSource timeoutCts = new CancellationTokenSource();
		if (timeoutMs != -1)
		{
			timeoutCts.CancelAfter(timeoutMs);
		}
		using CancellationTokenSource connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
		try
		{
			await client.ConnectAsync("127.0.0.1", 7432, connectCts.Token).ConfigureAwait(continueOnCapturedContext: false);
			return true;
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
		catch (SocketException)
		{
			return false;
		}
	}

	private static string? ReadToken()
	{
		try
		{
			string text = RuntimeSecurityService.ReadProtectedRuntimeTextFile(AppPaths.ApiTokenFile, Encoding.UTF8, 65536).Trim();
			return string.IsNullOrWhiteSpace(text) ? null : text;
		}
		catch
		{
			return null;
		}
	}

	private async Task<string?> GetAsync(string path, CancellationToken ct)
	{
		int? num = TryGetTrustedApiListenerPid();
		if (!num.HasValue)
		{
			return null;
		}
		string text = ReadToken();
		if (text == null)
		{
			return null;
		}
		if (TryGetTrustedApiListenerPid() != num)
		{
			return null;
		}
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Options.Set(ExpectedApiProcessId, num.Value);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", text);
		request.Headers.ConnectionClose = true;
		using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(continueOnCapturedContext: false);
		if (!response.IsSuccessStatusCode)
		{
			return null;
		}
		long? contentLength = response.Content.Headers.ContentLength;
		if (contentLength.HasValue && contentLength.GetValueOrDefault() > 16777216)
		{
			return null;
		}
		string result;
		await using (Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(continueOnCapturedContext: false))
		{
			using MemoryStream buffer = new MemoryStream();
			byte[] chunk = new byte[65536];
			while (true)
			{
				int num2 = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(continueOnCapturedContext: false);
				if (num2 != 0)
				{
					if (buffer.Length + num2 > 16777216)
					{
						result = null;
						break;
					}
					buffer.Write(chunk, 0, num2);
					continue;
				}
				result = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
				break;
			}
		}
		return result;
	}

	private static async ValueTask<Stream> ConnectTrustedApiAsync(SocketsHttpConnectionContext context, CancellationToken ct)
	{
		if (!string.Equals(context.DnsEndPoint.Host, "127.0.0.1", StringComparison.Ordinal) || context.DnsEndPoint.Port != 7432 || !context.InitialRequestMessage.Options.TryGetValue(ExpectedApiProcessId, out var expectedPid))
		{
			throw new HttpRequestException("Control API endpoint/process binding is missing.");
		}
		Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
		{
			NoDelay = true
		};
		try
		{
			await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 7432), ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!(socket.LocalEndPoint is IPEndPoint iPEndPoint) || !IsExactTrustedEstablishedApiConnection(iPEndPoint.Port, expectedPid))
			{
				throw new HttpRequestException("Control API connection is not owned by the installed p2p-agent process.");
			}
			return new NetworkStream(socket, ownsSocket: true);
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}

	private static int? TryGetTrustedApiListenerPid()
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}
		int size = 0;
		if (GetExtendedTcpTable(IntPtr.Zero, ref size, order: false, 2, TcpTableClass.OwnerPidListener, 0u) != 122 || size < 4 || size > 4194304)
		{
			return null;
		}
		nint num = Marshal.AllocHGlobal(size);
		try
		{
			if (GetExtendedTcpTable(num, ref size, order: false, 2, TcpTableClass.OwnerPidListener, 0u) != 0 || size < 4)
			{
				return null;
			}
			int num2 = Marshal.ReadInt32(num);
			int num3 = Marshal.SizeOf<MibTcpRowOwnerPid>();
			if (num2 < 0 || 4 + (long)num2 * (long)num3 > size)
			{
				return null;
			}
			int? result = null;
			for (int i = 0; i < num2; i++)
			{
				MibTcpRowOwnerPid mibTcpRowOwnerPid = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(num, 4 + i * num3));
				checked
				{
					if (mibTcpRowOwnerPid.State == 2 && DecodeNetworkPort(mibTcpRowOwnerPid.LocalPort) == 7432 && IsIpv4Loopback(mibTcpRowOwnerPid.LocalAddress))
					{
						if (!IsExactAgentProcess(mibTcpRowOwnerPid.OwningPid))
						{
							return null;
						}
						if (result.HasValue && result.Value != (int)mibTcpRowOwnerPid.OwningPid)
						{
							return null;
						}
						result = (int)mibTcpRowOwnerPid.OwningPid;
					}
				}
			}
			return result;
		}
		catch
		{
			return null;
		}
		finally
		{
			Marshal.FreeHGlobal(num);
		}
	}

	private static bool IsExactTrustedEstablishedApiConnection(int clientLocalPort, int expectedPid)
	{
		bool flag = !OperatingSystem.IsWindows();
		if (!flag)
		{
			bool flag2 = ((clientLocalPort <= 0 || clientLocalPort > 65535) ? true : false);
			flag = flag2;
		}
		if (flag || expectedPid <= 0 || !IsExactAgentProcess(checked((uint)expectedPid)))
		{
			return false;
		}
		int size = 0;
		if (GetExtendedTcpTable(IntPtr.Zero, ref size, order: false, 2, TcpTableClass.OwnerPidAll, 0u) != 122 || size < 4 || size > 4194304)
		{
			return false;
		}
		nint num = Marshal.AllocHGlobal(size);
		try
		{
			if (GetExtendedTcpTable(num, ref size, order: false, 2, TcpTableClass.OwnerPidAll, 0u) != 0 || size < 4)
			{
				flag = false;
			}
			else
			{
				int num2 = Marshal.ReadInt32(num);
				int num3 = Marshal.SizeOf<MibTcpRowOwnerPid>();
				if (num2 < 0 || 4 + (long)num2 * (long)num3 > size)
				{
					flag = false;
				}
				else
				{
					int num4 = 0;
					while (true)
					{
						if (num4 < num2)
						{
							MibTcpRowOwnerPid mibTcpRowOwnerPid = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(num, 4 + num4 * num3));
							if (mibTcpRowOwnerPid.State == 5 && IsIpv4Loopback(mibTcpRowOwnerPid.LocalAddress) && DecodeNetworkPort(mibTcpRowOwnerPid.LocalPort) == 7432 && IsIpv4Loopback(mibTcpRowOwnerPid.RemoteAddress) && DecodeNetworkPort(mibTcpRowOwnerPid.RemotePort) == clientLocalPort && mibTcpRowOwnerPid.OwningPid == checked((uint)expectedPid))
							{
								flag = IsExactAgentProcess(mibTcpRowOwnerPid.OwningPid);
								break;
							}
							num4++;
							continue;
						}
						flag = false;
						break;
					}
				}
			}
		}
		catch
		{
			flag = false;
		}
		finally
		{
			Marshal.FreeHGlobal(num);
		}
		return flag;
	}

	private static int DecodeNetworkPort(uint value)
	{
		byte[] bytes = BitConverter.GetBytes(value);
		return (bytes[0] << 8) | bytes[1];
	}

	private static bool IsIpv4Loopback(uint value)
	{
		byte[] bytes = BitConverter.GetBytes(value);
		if (bytes != null && bytes.Length == 4 && bytes[0] == 127 && bytes[1] == 0 && bytes[2] == 0)
		{
			return bytes[3] == 1;
		}
		return false;
	}

	private static bool IsExactAgentProcess(uint pid)
	{
		if (pid == 0 || pid > int.MaxValue)
		{
			return false;
		}
		try
		{
			using Process process = Process.GetProcessById(checked((int)pid));
			if (process.HasExited || !string.Equals(process.ProcessName, "p2p-agent", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			string text = process.MainModule?.FileName;
			return text != null && string.Equals(Path.GetFullPath(text).TrimEnd('\\', '/'), Path.GetFullPath(AppPaths.AgentExe).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	public async Task<AgentInfo?> GetInfoAsync(CancellationToken ct = default(CancellationToken))
	{
		try
		{
			string text = await GetAsync("/v1/info", ct).ConfigureAwait(continueOnCapturedContext: false);
			if (text == null)
			{
				return null;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(text);
			JsonElement element = Unwrap(jsonDocument.RootElement);
			string text2 = PickString(element, "peer_id", "peerId", "id");
			string text3 = PickString(element, "version");
			string text4 = PickString(element, "agent_name", "name");
			string text5 = PickString(element, "relay_peer_id");
			if (!AgentConfigService.IsValidPeerId(text2) || string.IsNullOrWhiteSpace(text3))
			{
				return null;
			}
			return new AgentInfo(text2 ?? "", text3 ?? "", text4 ?? "", text5 ?? "", PickBool(element, "reservation_ready"), PickBool(element, "mdns_ready"), PickInt(element, "connected_remote_count"), PickInt(element, "running_tasks"), PickInt(element, "available_task_slots"), PickStringArray(element, "listen_addresses", "addresses", "addrs"), Prettify(text));
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return null;
		}
	}

	public async Task<string?> GetPeersRawAsync(CancellationToken ct = default(CancellationToken))
	{
		try
		{
			string text = await GetAsync("/v1/peers", ct).ConfigureAwait(continueOnCapturedContext: false);
			return (text == null) ? null : Prettify(text);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return null;
		}
	}

	public async Task<PeerQueryResult> GetPeersResultAsync(CancellationToken ct = default(CancellationToken))
	{
		List<PeerEntry> result = new List<PeerEntry>();
		try
		{
			string text = await GetAsync("/v1/peers", ct).ConfigureAwait(continueOnCapturedContext: false);
			if (text == null)
			{
				return new PeerQueryResult(Success: false, "request_failed", result);
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(text);
			JsonElement? jsonElement = FindPeersArray(jsonDocument.RootElement);
			if (!jsonElement.HasValue)
			{
				return new PeerQueryResult(Success: false, "peers_array_missing", result);
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
			foreach (JsonElement item in jsonElement.Value.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Object)
				{
					return new PeerQueryResult(Success: false, "peer_entry_invalid", Array.Empty<PeerEntry>());
				}
				string text2 = PickString(item, "peer_id", "peerId", "id") ?? "";
				string name = PickString(item, "agent_name", "name") ?? "";
				string connectionPath = PickString(item, "path", "connection_path", "delivery_path") ?? "";
				string scope = PickString(item, "scope", "role", "kind") ?? "";
				if (!AgentConfigService.IsValidPeerId(text2))
				{
					return new PeerQueryResult(Success: false, "peer_id_invalid", Array.Empty<PeerEntry>());
				}
				if (!hashSet.Add(text2))
				{
					return new PeerQueryResult(Success: false, "peer_id_duplicate", Array.Empty<PeerEntry>());
				}
				result.Add(new PeerEntry(text2, name, connectionPath, scope));
			}
			return new PeerQueryResult(Success: true, "", result);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (JsonException)
		{
			return new PeerQueryResult(Success: false, "json_invalid", Array.Empty<PeerEntry>());
		}
		catch
		{
			return new PeerQueryResult(Success: false, "read_failed", Array.Empty<PeerEntry>());
		}
	}

	public async Task<IReadOnlyList<PeerEntry>> GetPeersAsync(CancellationToken ct = default(CancellationToken))
	{
		PeerQueryResult peerQueryResult = await GetPeersResultAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		IReadOnlyList<PeerEntry> result;
		if (!peerQueryResult.Success)
		{
			IReadOnlyList<PeerEntry> readOnlyList = Array.Empty<PeerEntry>();
			result = readOnlyList;
		}
		else
		{
			result = peerQueryResult.Peers;
		}
		return result;
	}

	private static JsonElement Unwrap(JsonElement root)
	{
		string[] array = new string[3] { "result", "data", "info" };
		foreach (string propertyName in array)
		{
			if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Object)
			{
				return value;
			}
		}
		return root;
	}

	private static JsonElement? FindPeersArray(JsonElement root)
	{
		if (root.ValueKind == JsonValueKind.Array)
		{
			return root;
		}
		if (root.ValueKind != JsonValueKind.Object)
		{
			return null;
		}
		string[] array = new string[2] { "peers", "nodes" };
		foreach (string propertyName in array)
		{
			if (root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array)
			{
				return value;
			}
		}
		array = new string[2] { "result", "data" };
		foreach (string propertyName2 in array)
		{
			if (!root.TryGetProperty(propertyName2, out var value2) || value2.ValueKind != JsonValueKind.Object)
			{
				continue;
			}
			string[] array2 = new string[2] { "peers", "nodes" };
			foreach (string propertyName3 in array2)
			{
				if (value2.TryGetProperty(propertyName3, out var value3) && value3.ValueKind == JsonValueKind.Array)
				{
					return value3;
				}
			}
		}
		return null;
	}

	private static string? PickString(JsonElement element, params string[] candidates)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			return null;
		}
		foreach (string propertyName in candidates)
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
				return value.ToString();
			}
		}
		return null;
	}

	private static bool? PickBool(JsonElement element, params string[] candidates)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			return null;
		}
		foreach (string propertyName in candidates)
		{
			if (element.TryGetProperty(propertyName, out var value))
			{
				if (value.ValueKind == JsonValueKind.True)
				{
					return true;
				}
				if (value.ValueKind == JsonValueKind.False)
				{
					return false;
				}
			}
		}
		return null;
	}

	private static int? PickInt(JsonElement element, params string[] candidates)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			return null;
		}
		foreach (string propertyName in candidates)
		{
			if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var value2))
			{
				return value2;
			}
		}
		return null;
	}

	private static IReadOnlyList<string> PickStringArray(JsonElement element, params string[] candidates)
	{
		List<string> list = new List<string>();
		if (element.ValueKind != JsonValueKind.Object)
		{
			return list;
		}
		foreach (string propertyName in candidates)
		{
			if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
			{
				continue;
			}
			foreach (JsonElement item in value.EnumerateArray())
			{
				if (item.ValueKind == JsonValueKind.String)
				{
					string text = item.GetString();
					if (!string.IsNullOrWhiteSpace(text))
					{
						list.Add(text);
					}
				}
			}
			if (list.Count > 0)
			{
				break;
			}
		}
		return list;
	}

	private static string Prettify(string json)
	{
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			return JsonSerializer.Serialize(jsonDocument.RootElement, PrettyOptions);
		}
		catch
		{
			return json;
		}
	}

	[DllImport("iphlpapi.dll", SetLastError = true)]
	private static extern uint GetExtendedTcpTable(nint tcpTable, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, TcpTableClass tableClass, uint reserved);

	public void Dispose()
	{
		_http.Dispose();
	}
}
