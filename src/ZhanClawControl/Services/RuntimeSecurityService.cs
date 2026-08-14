#nullable disable warnings
#pragma warning disable CS0649
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ZhanClawControl.Services;

public static class RuntimeSecurityService
{
	public enum TaskMaintenancePhase
	{
		Mutation,
		ValidationReady
	}

	public sealed record MaintenanceStartAuthorization(bool IsMaintenance, bool AllowTrustedRollbackPayload, string? AgentSha256);

	private sealed record ParsedMaintenanceStartPermit(SecurityIdentifier RunAsSid, bool AllowTrustedRollbackPayload, string AgentSha256);

	private enum AgentExecutionAclState
	{
		Invalid,
		Normal,
		Restricted
	}

	private sealed class PayloadManifest
	{
		[JsonPropertyName("schema_version")]
		public int SchemaVersion { get; set; }

		[JsonPropertyName("file_name")]
		public string FileName { get; set; } = "";

		[JsonPropertyName("sha256")]
		public string Sha256 { get; set; } = "";

		[JsonPropertyName("version")]
		public string Version { get; set; } = "";

		[JsonPropertyName("pe_machine")]
		public string PeMachine { get; set; } = "";

		[JsonPropertyName("pe_subsystem")]
		public string PeSubsystem { get; set; } = "";

		[JsonPropertyName("require_authenticode_valid")]
		public bool RequireAuthenticodeValid { get; set; }

		[JsonPropertyName("expected_signer_common_name")]
		public string ExpectedSignerCommonName { get; set; } = "";

		[JsonPropertyName("expected_leaf_certificate_sha256")]
		public string ExpectedLeafCertificateSha256 { get; set; } = "";

		[JsonPropertyName("expected_spki_sha256")]
		public string ExpectedSpkiSha256 { get; set; } = "";

		[JsonPropertyName("trusted_rollback_spki_sha256")]
		public List<string> TrustedRollbackSpkiSha256 { get; set; } = new List<string>();
	}

	private enum FileInfoByHandleClass
	{
		FileDispositionInfo = 4,
		FileAttributeTagInfo = 9
	}

	private struct FileDispositionInfo
	{
		[MarshalAs(UnmanagedType.Bool)]
		public bool DeleteFile;
	}

	private struct LocalGroupUsersInfo0
	{
		public nint Name;
	}

	private struct FileAttributeTagInfo
	{
		public uint FileAttributes;

		public uint ReparseTag;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct WinTrustFileInfoNative
	{
		public uint StructSize;

		public nint FilePath;

		public nint FileHandle;

		public nint KnownSubject;
	}

	private sealed class WinTrustFileInfo : IDisposable
	{
		private readonly nint _path;

		public nint Pointer { get; }

		public WinTrustFileInfo(string path)
		{
			_path = Marshal.StringToCoTaskMemUni(path);
			WinTrustFileInfoNative structure = new WinTrustFileInfoNative
			{
				StructSize = (uint)Marshal.SizeOf<WinTrustFileInfoNative>(),
				FilePath = _path
			};
			Pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfoNative>());
			Marshal.StructureToPtr(structure, Pointer, fDeleteOld: false);
		}

		public void Dispose()
		{
			Marshal.FreeCoTaskMem(Pointer);
			Marshal.FreeCoTaskMem(_path);
		}
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct WinTrustDataNative
	{
		public uint StructSize;

		public nint PolicyCallbackData;

		public nint SIPClientData;

		public uint UIChoice;

		public uint RevocationChecks;

		public uint UnionChoice;

		public nint FileInfo;

		public uint StateAction;

		public nint StateData;

		public string? URLReference;

		public uint ProviderFlags;

		public uint UIContext;

		public nint SignatureSettings;
	}

	private enum SidNameUse
	{
		User = 1,
		Group,
		Domain,
		Alias,
		WellKnownGroup,
		DeletedAccount,
		Invalid,
		Unknown,
		Computer,
		Label,
		LogonSession
	}

	private const uint DaclSecurityInformation = 4u;

	private const uint OwnerSecurityInformation = 1u;

	private const uint ProtectedDaclSecurityInformation = 2147483648u;

	private const uint SddlRevision1 = 1u;

	private const uint GenericRead = 2147483648u;

	private const uint GenericWrite = 1073741824u;

	private const uint DeleteAccess = 65536u;

	private const uint ReadControl = 131072u;

	private const uint FileReadAttributes = 128u;

	private const uint FileShareRead = 1u;

	private const uint FileShareWrite = 2u;

	private const uint FileShareDelete = 4u;

	private const uint OpenExisting = 3u;

	private const uint FileFlagBackupSemantics = 33554432u;

	private const uint FileFlagOpenReparsePoint = 2097152u;

	private const int ErrorFileNotFound = 2;

	private const int ErrorPathNotFound = 3;

	private const int ErrorAccessDenied = 5;

	private const int FileFullControlMask = 2032127;

	private const int FileReadExecuteMask = 1179817;

	private const int FileReadDeleteMask = 1245321;

	private static readonly TimeSpan MaintenanceStartPermitLifetime = TimeSpan.FromMinutes(2.0);

	private static readonly Guid WinTrustActionGenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

	public static bool HasMaintenanceArtifacts
	{
		get
		{
			if (!File.Exists(AppPaths.TaskMaintenanceMarker) && !Directory.Exists(AppPaths.TaskMaintenanceMarker) && !File.Exists(AppPaths.UninstallRecoveryRoot) && !Directory.Exists(AppPaths.UninstallRecoveryRoot) && !File.Exists(AppPaths.UninstallRecoveryStageRoot) && !Directory.Exists(AppPaths.UninstallRecoveryStageRoot) && !File.Exists(AppPaths.UninstallRecoveryCleanupRoot))
			{
				return Directory.Exists(AppPaths.UninstallRecoveryCleanupRoot);
			}
			return true;
		}
	}

	public static bool HasMaintenanceStartPermitObject
	{
		get
		{
			if (!File.Exists(AppPaths.MaintenanceStartPermit))
			{
				return Directory.Exists(AppPaths.MaintenanceStartPermit);
			}
			return true;
		}
	}

	public static string ExpectedAgentVersion => LoadPayloadManifest().Version;

	public static SecurityIdentifier ResolveAccountSid(string account)
	{
		if (string.IsNullOrWhiteSpace(account))
		{
			throw new InvalidDataException("运行账户不能为空。");
		}
		string text = account.Trim();
		try
		{
			if (text.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
			{
				return new SecurityIdentifier(text);
			}
			return (SecurityIdentifier)new NTAccount(text).Translate(typeof(SecurityIdentifier));
		}
		catch (Exception ex) when (((ex is IdentityNotMappedException || ex is ArgumentException) ? 1 : 0) != 0)
		{
			throw new InvalidDataException("无法解析运行账户：" + text, ex);
		}
	}

	public static SecurityIdentifier ResolveInteractiveUserSid(string account)
	{
		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException("运行账户类型校验仅支持 Windows。");
		}
		SecurityIdentifier securityIdentifier = ResolveAccountSid(account);
		SecurityIdentifier sid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
		SecurityIdentifier sid2 = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
		SecurityIdentifier sid3 = new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
		SecurityIdentifier sid4 = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
		if (securityIdentifier.Equals(sid) || securityIdentifier.Equals(sid2) || securityIdentifier.Equals(sid3) || securityIdentifier.Equals(sid4))
		{
			throw new InvalidDataException("运行账户必须是实际的交互式用户，不能使用 Administrators、SYSTEM 或服务账户。");
		}
		byte[] array = new byte[securityIdentifier.BinaryLength];
		securityIdentifier.GetBinaryForm(array, 0);
		uint nameLength = 0u;
		uint referencedDomainNameLength = 0u;
		LookupAccountSid(null, array, null, ref nameLength, null, ref referencedDomainNameLength, out var use);
		int lastWin32Error = Marshal.GetLastWin32Error();
		if (lastWin32Error != 122 || nameLength == 0)
		{
			throw new Win32Exception(lastWin32Error, "无法核验运行账户类型：" + securityIdentifier.Value);
		}
		checked
		{
			StringBuilder name = new StringBuilder((int)nameLength);
			StringBuilder referencedDomainName = ((referencedDomainNameLength != 0) ? new StringBuilder((int)referencedDomainNameLength) : null);
			if (!LookupAccountSid(null, array, name, ref nameLength, referencedDomainName, ref referencedDomainNameLength, out use))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核验运行账户类型：" + securityIdentifier.Value);
			}
			if (use != SidNameUse.User)
			{
				throw new InvalidDataException($"运行账户必须是实际用户，当前 SID 类型为 {use}：{securityIdentifier.Value}");
			}
			return securityIdentifier;
		}
	}

	public static void EnsureSafeInstallRoot()
	{
		string actual = Path.GetFullPath("C:\\Program Files\\P2PAgent").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (!(from value in new string[2]
			{
				Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
				Environment.GetEnvironmentVariable("ProgramW6432")
			}
			where !string.IsNullOrWhiteSpace(value)
			select Path.GetFullPath(Path.Combine(value, "P2PAgent")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Any((string expected) => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)))
		{
			throw new InvalidDataException("InstallRoot 未位于预期的 Program Files\\P2PAgent。");
		}
		if (Directory.Exists(actual))
		{
			RejectReparsePoint(actual);
		}
		else
		{
			Directory.CreateDirectory(actual);
		}
		RejectReparsePoint(actual);
		ApplyInstallRootDacl(actual);
		RejectReparsePoint(actual);
	}

	public static void PrepareSecureDataRoot(string runAsUser)
	{
		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException("DataRoot ACL 加固仅支持 Windows。");
		}
		SecurityIdentifier securityIdentifier = ResolveInteractiveUserSid(runAsUser);
		string root = Path.GetFullPath("C:\\ProgramData\\P2PAgent").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (!(from value in new string[2]
			{
				Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
				Environment.GetEnvironmentVariable("ProgramData")
			}
			where !string.IsNullOrWhiteSpace(value)
			select Path.GetFullPath(Path.Combine(value, "P2PAgent")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Any((string expected) => string.Equals(root, expected, StringComparison.OrdinalIgnoreCase)))
		{
			throw new InvalidDataException("DataRoot 路径解析结果异常。");
		}
		if (Directory.Exists(root))
		{
			RejectReparsePoint(root);
			RejectUntrustedExistingSecrets(root, securityIdentifier);
		}
		else
		{
			Directory.CreateDirectory(root);
		}
		ApplyExactDacl(root, securityIdentifier, isDirectory: true);
		RejectReparsePoint(root);
		Stack<string> stack = new Stack<string>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			string path = stack.Pop();
			RejectReparsePoint(path);
			ApplyExactDacl(path, securityIdentifier, isDirectory: true);
			RejectReparsePoint(path);
			foreach (string item in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly))
			{
				RejectReparsePoint(item);
				bool flag = Directory.Exists(item);
				ApplyExactDacl(item, securityIdentifier, flag);
				RejectReparsePoint(item);
				if (flag)
				{
					stack.Push(item);
				}
			}
		}
		RejectReparsePoint(root);
	}

	public static void ValidateExistingDataRootTrust(string runAsUser)
	{
		if (Directory.Exists("C:\\ProgramData\\P2PAgent"))
		{
			string fullPath = Path.GetFullPath("C:\\ProgramData\\P2PAgent");
			RejectReparsePoint(fullPath);
			RejectUntrustedExistingSecrets(fullPath, ResolveInteractiveUserSid(runAsUser));
			RejectReparsePoint(fullPath);
		}
	}

	public static bool ValidateExistingDataRootTrustAllowingLegacyEmbeddedSwarm(string runAsUser, string embeddedSwarmSha256)
	{
		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException("旧版 swarm.key ACL 迁移仅支持 Windows。");
		}
		if (!IsSha256Hex(embeddedSwarmSha256))
		{
			throw new InvalidDataException("内嵌 swarm.key SHA-256 无效。");
		}
		if (!Directory.Exists("C:\\ProgramData\\P2PAgent"))
		{
			return false;
		}
		string path = ValidateAndGetExpectedDataRoot();
		SecurityIdentifier runAsSid = ResolveInteractiveUserSid(runAsUser);
		HashSet<string> allowedSids = CreateRuntimeAllowedSids(runAsSid);
		RejectReparsePoint(path);
		ValidateExistingProtectedObject(path, allowedSids, "OICI");
		foreach (string item in new string[4]
		{
			AppPaths.ConfigFile,
			AppPaths.IdentityFile,
			AppPaths.ApiTokenFile,
			AppPaths.JournalFile
		}.Where((string path2) => File.Exists(path2) || Directory.Exists(path2)))
		{
			RejectReparsePoint(item);
			if (Directory.Exists(item))
			{
				throw new UnauthorizedAccessException("敏感文件路径被目录占用：" + item);
			}
			ValidateExistingProtectedObject(item, allowedSids, "", allowExactInheritance: true);
			RejectReparsePoint(item);
		}
		if (!File.Exists(AppPaths.SwarmKeyFile) && !Directory.Exists(AppPaths.SwarmKeyFile))
		{
			RejectReparsePoint(path);
			return false;
		}
		RejectReparsePoint(AppPaths.SwarmKeyFile);
		if (Directory.Exists(AppPaths.SwarmKeyFile))
		{
			throw new UnauthorizedAccessException("敏感文件路径被目录占用：" + AppPaths.SwarmKeyFile);
		}
		try
		{
			ValidateExistingProtectedObject(AppPaths.SwarmKeyFile, allowedSids, "", allowExactInheritance: true);
			RejectReparsePoint(path);
			return false;
		}
		catch (UnauthorizedAccessException) when (IsExactLegacyInstallRootInheritedFileAcl(AppPaths.SwarmKeyFile, runAsSid))
		{
		}
		ValidateSwarmKey(AppPaths.SwarmKeyFile);
		ValidateFileSha256(AppPaths.SwarmKeyFile, embeddedSwarmSha256, "旧版 swarm.key 与当前内嵌密钥不一致");
		RejectReparsePoint(AppPaths.SwarmKeyFile);
		RejectReparsePoint(path);
		return true;
	}

	public static void MigrateLegacyEmbeddedSwarmAcl(string runAsUser, string embeddedSwarmSha256)
	{
		if (ValidateExistingDataRootTrustAllowingLegacyEmbeddedSwarm(runAsUser, embeddedSwarmSha256))
		{
			ProtectAndValidateRuntimeFile(AppPaths.SwarmKeyFile, runAsUser);
			ValidateSwarmKey(AppPaths.SwarmKeyFile);
			ValidateFileSha256(AppPaths.SwarmKeyFile, embeddedSwarmSha256, "旧版 swarm.key 在 ACL 迁移期间发生变化");
		}
		ValidateExistingDataRootTrust(runAsUser);
	}

	public static void ProtectAndValidateRuntimeFile(string path, string runAsUser)
	{
		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException("运行文件 ACL 加固仅支持 Windows。");
		}
		if (!File.Exists(path))
		{
			throw new FileNotFoundException("待加固的运行文件不存在。", path);
		}
		SecurityIdentifier securityIdentifier = ResolveInteractiveUserSid(runAsUser);
		RejectReparsePoint(path);
		ApplyExactDacl(path, securityIdentifier, isDirectory: false);
		RejectReparsePoint(path);
		HashSet<string> allowedSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			securityIdentifier.Value,
			new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
			new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value
		};
		ValidateExistingProtectedObject(path, allowedSids, "");
		RejectReparsePoint(path);
	}

	private static void RejectUntrustedExistingSecrets(string root, SecurityIdentifier runAsSid)
	{
		string[] source = new string[5]
		{
			AppPaths.ConfigFile,
			AppPaths.SwarmKeyFile,
			AppPaths.IdentityFile,
			AppPaths.ApiTokenFile,
			AppPaths.JournalFile
		};
		HashSet<string> allowedSids = CreateRuntimeAllowedSids(runAsSid);
		List<string> list = source.Where((string path) => File.Exists(path) || Directory.Exists(path)).ToList();
		if (list.Count == 0)
		{
			return;
		}
		ValidateExistingProtectedObject(root, allowedSids, "OICI");
		foreach (string item in list)
		{
			RejectReparsePoint(item);
			if (Directory.Exists(item))
			{
				throw new UnauthorizedAccessException("敏感文件路径被目录占用：" + item);
			}
			ValidateExistingProtectedObject(item, allowedSids, "", allowExactInheritance: true);
			RejectReparsePoint(item);
		}
	}

	private static HashSet<string> CreateRuntimeAllowedSids(SecurityIdentifier runAsSid)
	{
		return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			runAsSid.Value,
			new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
			new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value
		};
	}

	private static string ValidateAndGetExpectedDataRoot()
	{
		string root = Path.GetFullPath("C:\\ProgramData\\P2PAgent").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (!(from value in new string[2]
			{
				Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
				Environment.GetEnvironmentVariable("ProgramData")
			}
			where !string.IsNullOrWhiteSpace(value)
			select Path.GetFullPath(Path.Combine(value, "P2PAgent")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Any((string expected) => string.Equals(root, expected, StringComparison.OrdinalIgnoreCase)))
		{
			throw new InvalidDataException("DataRoot 路径解析结果异常。");
		}
		RejectReparsePoint(root);
		return root;
	}

	private static bool IsExactLegacyInstallRootInheritedFileAcl(string path, SecurityIdentifier runAsSid)
	{
		if (!TryReadDaclSddl(path, out string daclSddl, out string ownerSid) || !daclSddl.StartsWith("D:AI", StringComparison.Ordinal))
		{
			return false;
		}
		string value = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
		string value2 = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
		string value3 = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value;
		string b = WindowsIdentity.GetCurrent().User?.Value;
		if (!string.Equals(ownerSid, value, StringComparison.OrdinalIgnoreCase) && !string.Equals(ownerSid, value2, StringComparison.OrdinalIgnoreCase) && !string.Equals(ownerSid, runAsSid.Value, StringComparison.OrdinalIgnoreCase) && !string.Equals(ownerSid, b, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		MatchCollection matchCollection = Regex.Matches(daclSddl, "\\((?<type>[^;]*);(?<flags>[^;]*);(?<rights>[^;]*);[^;]*;[^;]*;(?<sid>[^)]*)\\)");
		if (matchCollection.Count != 3)
		{
			return false;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (Match item in matchCollection)
		{
			if (item.Groups["type"].Value != "A" || item.Groups["flags"].Value != "ID")
			{
				return false;
			}
			string text;
			try
			{
				string value4 = item.Groups["sid"].Value;
				text = value4 switch
				{
					"BA" => value, 
					"SY" => value2, 
					"BU" => value3, 
					_ => new SecurityIdentifier(value4).Value, 
				};
			}
			catch
			{
				return false;
			}
			if (!hashSet.Add(text))
			{
				return false;
			}
			string b2 = (string.Equals(text, value3, StringComparison.OrdinalIgnoreCase) ? "0x1200a9" : "FA");
			if (!string.Equals(item.Groups["rights"].Value, b2, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (!string.Equals(text, value, StringComparison.OrdinalIgnoreCase) && !string.Equals(text, value2, StringComparison.OrdinalIgnoreCase) && !string.Equals(text, value3, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}
		if (hashSet.Contains(value) && hashSet.Contains(value2))
		{
			return hashSet.Contains(value3);
		}
		return false;
	}

	private static void ValidateFileSha256(string path, string expectedSha256, string message)
	{
		RejectReparsePoint(path);
		using FileStream source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		string text = Convert.ToHexString(SHA256.HashData(source));
		if (!string.Equals(text, expectedSha256, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException($"{message}。expected={expectedSha256} actual={text}");
		}
	}

	private static bool IsSha256Hex(string value)
	{
		if (value.Length == 64)
		{
			return value.All(Uri.IsHexDigit);
		}
		return false;
	}

	private static void ValidateExistingProtectedObject(string path, IReadOnlySet<string> allowedSids, string expectedAceFlags, bool allowExactInheritance = false)
	{
		if (!TryReadDaclSddl(path, out string daclSddl, out string ownerSid) || !allowedSids.Contains(ownerSid) || (!IsExactProtectedDacl(daclSddl, allowedSids, expectedAceFlags) && (!allowExactInheritance || !IsExactInheritedFileDacl(daclSddl, allowedSids))))
		{
			throw new UnauthorizedAccessException("敏感运行对象的 owner/DACL 无法证明安全：" + path + "。请显式清理或安全迁移后重试。");
		}
	}

	private static bool IsExactInheritedFileDacl(string sddl, IReadOnlySet<string> allowedSids)
	{
		if (!sddl.StartsWith("D:AI", StringComparison.Ordinal))
		{
			return false;
		}
		MatchCollection matchCollection = Regex.Matches(sddl, "\\((?<type>[^;]*);(?<flags>[^;]*);(?<rights>[^;]*);[^;]*;[^;]*;(?<sid>[^)]*)\\)");
		if (matchCollection.Count != allowedSids.Count)
		{
			return false;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (Match item2 in matchCollection)
		{
			if (item2.Groups["type"].Value != "A" || item2.Groups["flags"].Value != "ID" || item2.Groups["rights"].Value != "FA")
			{
				return false;
			}
			string item;
			try
			{
				string value = item2.Groups["sid"].Value;
				string text = ((value == "BA") ? new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value : ((!(value == "SY")) ? new SecurityIdentifier(value).Value : new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value));
				item = text;
			}
			catch
			{
				return false;
			}
			if (!allowedSids.Contains(item) || !hashSet.Add(item))
			{
				return false;
			}
		}
		return allowedSids.All(hashSet.Contains);
	}

	private static bool TryReadDaclSddl(string path, out string daclSddl, out string ownerSid)
	{
		daclSddl = "";
		ownerSid = "";
		nint securityDescriptor = IntPtr.Zero;
		nint owner = IntPtr.Zero;
		nint group = IntPtr.Zero;
		nint dacl = IntPtr.Zero;
		nint sacl = IntPtr.Zero;
		try
		{
			if (GetNamedSecurityInfo(path, 1, 5u, out owner, out group, out dacl, out sacl, out securityDescriptor) != 0 || securityDescriptor == IntPtr.Zero)
			{
				return false;
			}
			if (owner == IntPtr.Zero || !ConvertSidToStringSid(owner, out var stringSid))
			{
				return false;
			}
			try
			{
				ownerSid = Marshal.PtrToStringUni(stringSid) ?? "";
			}
			finally
			{
				LocalFree(stringSid);
			}
			if (ownerSid.Length == 0)
			{
				return false;
			}
			if (!ConvertSecurityDescriptorToStringSecurityDescriptor(securityDescriptor, 1u, 4u, out var stringSecurityDescriptor, out var _))
			{
				return false;
			}
			try
			{
				daclSddl = Marshal.PtrToStringUni(stringSecurityDescriptor) ?? "";
				return daclSddl.Length > 0;
			}
			finally
			{
				LocalFree(stringSecurityDescriptor);
			}
		}
		finally
		{
			if (securityDescriptor != IntPtr.Zero)
			{
				LocalFree(securityDescriptor);
			}
		}
	}

	private static bool IsExactProtectedDacl(string sddl, IReadOnlySet<string> allowedSids, string expectedAceFlags)
	{
		if (!sddl.StartsWith("D:P", StringComparison.Ordinal))
		{
			return false;
		}
		MatchCollection matchCollection = Regex.Matches(sddl, "\\((?<type>[^;]*);(?<flags>[^;]*);(?<rights>[^;]*);[^;]*;[^;]*;(?<sid>[^)]*)\\)");
		if (matchCollection.Count != allowedSids.Count)
		{
			return false;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (Match item2 in matchCollection)
		{
			if (!string.Equals(item2.Groups["type"].Value, "A", StringComparison.Ordinal) || !string.Equals(item2.Groups["rights"].Value, "FA", StringComparison.Ordinal) || !string.Equals(item2.Groups["flags"].Value, expectedAceFlags, StringComparison.Ordinal))
			{
				return false;
			}
			string value = item2.Groups["sid"].Value;
			string item;
			try
			{
				string text = ((value == "BA") ? new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value : ((!(value == "SY")) ? new SecurityIdentifier(value).Value : new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value));
				item = text;
			}
			catch
			{
				return false;
			}
			if (!allowedSids.Contains(item) || !hashSet.Add(item))
			{
				return false;
			}
		}
		return allowedSids.All(hashSet.Contains);
	}

	public static void ValidateSecureDataRootForWrite()
	{
		string path = Path.GetFullPath("C:\\ProgramData\\P2PAgent").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (!Directory.Exists(path))
		{
			throw new DirectoryNotFoundException("受保护的数据目录不存在。");
		}
		RejectReparsePoint(path);
		if (!TryReadDaclSddl(path, out string daclSddl, out string ownerSid))
		{
			throw new UnauthorizedAccessException("无法验证数据目录 owner/DACL。");
		}
		HashSet<string> hashSet = ParseExactFullControlSids(daclSddl, "OICI");
		string administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
		string system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
		if (hashSet == null || hashSet.Count != 3 || !hashSet.Contains(administrators) || !hashSet.Contains(system) || !string.Equals(ownerSid, administrators, StringComparison.OrdinalIgnoreCase) || hashSet.Count((string sid) => !string.Equals(sid, administrators, StringComparison.OrdinalIgnoreCase) && !string.Equals(sid, system, StringComparison.OrdinalIgnoreCase)) != 1)
		{
			throw new UnauthorizedAccessException("数据目录不是受保护的三主体专用 ACL。");
		}
		RejectReparsePoint(path);
	}

	public static FileStream OpenProtectedRuntimeFileForRead(string path)
	{
		SafeFileHandle safeFileHandle = OpenProtectedRuntimeFileHandle(path, 2147614848u, 7u);
		try
		{
			return new FileStream(safeFileHandle, FileAccess.Read, 65536, isAsync: false);
		}
		catch
		{
			safeFileHandle.Dispose();
			throw;
		}
	}

	public static string ReadProtectedRuntimeTextFile(string path, Encoding encoding, int maxBytes)
	{
		ArgumentNullException.ThrowIfNull(encoding, "encoding");
		if (maxBytes <= 0)
		{
			throw new ArgumentOutOfRangeException("maxBytes");
		}
		using SafeFileHandle handle = OpenProtectedRuntimeFileHandle(path, 2147614848u, 5u);
		using FileStream fileStream = new FileStream(handle, FileAccess.Read, 16384, isAsync: false);
		if (fileStream.Length > maxBytes)
		{
			throw new InvalidDataException($"受保护运行文件超过 {maxBytes} 字节读取上限：{path}");
		}
		int num = checked((int)fileStream.Length);
		byte[] buffer = new byte[num];
		int num2;
		for (int i = 0; i < num; i += num2)
		{
			num2 = fileStream.Read(buffer, i, num - i);
			if (num2 == 0)
			{
				throw new IOException("受保护运行文件在读取期间被截断：" + path);
			}
		}
		using MemoryStream stream = new MemoryStream(buffer, writable: false);
		using StreamReader streamReader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, 4096, leaveOpen: false);
		return streamReader.ReadToEnd();
	}

	public static bool TryGetProtectedRuntimeFileLength(string path, out long length)
	{
		try
		{
			using FileStream fileStream = OpenProtectedRuntimeFileForRead(path);
			length = fileStream.Length;
			return true;
		}
		catch (FileNotFoundException)
		{
			length = 0L;
			return false;
		}
		catch (DirectoryNotFoundException)
		{
			length = 0L;
			return false;
		}
	}

	public static void CopyProtectedRuntimeFile(string sourcePath, string destinationPath, bool overwrite)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath, "destinationPath");
		string fullPath = Path.GetFullPath(sourcePath);
		string fullPath2 = Path.GetFullPath(destinationPath);
		if (string.Equals(fullPath.TrimEnd('\\', '/'), fullPath2.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
		{
			throw new IOException("导出目标不能是运行文件本身。");
		}
		string text = Path.Combine(Path.GetDirectoryName(fullPath2) ?? throw new InvalidDataException("导出目标目录无效。"), $".{Path.GetFileName(fullPath2)}.{Guid.NewGuid():N}.tmp");
		try
		{
			using (FileStream fileStream = OpenProtectedRuntimeFileForRead(fullPath))
			{
				using FileStream fileStream2 = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
				long num = fileStream.Length;
				byte[] array = new byte[65536];
				while (num > 0)
				{
					int num2 = fileStream.Read(array, 0, (int)Math.Min(array.Length, num));
					if (num2 == 0)
					{
						throw new IOException("导出源文件在复制期间被截断。");
					}
					fileStream2.Write(array, 0, num2);
					num -= num2;
				}
				fileStream2.Flush(flushToDisk: true);
			}
			File.Move(text, fullPath2, overwrite);
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

	public static void TruncateProtectedRuntimeFile(string path)
	{
		using SafeFileHandle handle = OpenProtectedRuntimeFileHandle(path, 1073873024u, 1u);
		using FileStream fileStream = new FileStream(handle, FileAccess.Write, 4096, isAsync: false);
		fileStream.SetLength(0L);
		fileStream.Flush(flushToDisk: true);
	}

	private static SafeFileHandle OpenProtectedRuntimeFileHandle(string path, uint desiredAccess, uint shareMode)
	{
		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException("受保护运行文件句柄校验仅支持 Windows。");
		}
		string text = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string text2 = Path.GetFullPath("C:\\ProgramData\\P2PAgent").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string value = text2 + Path.DirectorySeparatorChar;
		if (!text.StartsWith(value, StringComparison.OrdinalIgnoreCase))
		{
			throw new UnauthorizedAccessException("运行文件必须位于受保护的 DataRoot 内。");
		}
		HashSet<string> hashSet = ValidateProtectedRuntimeAncestors(text2, text);
		SafeFileHandle safeFileHandle = OpenExistingNoFollow(text, desiredAccess, shareMode, expectDirectory: false);
		try
		{
			ValidateOpenedHandlePathAndType(safeFileHandle, text, expectDirectory: false);
			var (sddl, item) = ReadHandleDaclSddl(safeFileHandle, text);
			if (!hashSet.Contains(item) || (!IsExactProtectedDacl(sddl, hashSet, "") && !IsExactInheritedFileDacl(sddl, hashSet)))
			{
				throw new UnauthorizedAccessException("运行文件的句柄 owner/DACL 无法证明安全：" + text);
			}
			return safeFileHandle;
		}
		catch
		{
			safeFileHandle.Dispose();
			throw;
		}
	}

	private static HashSet<string> ValidateProtectedRuntimeAncestors(string root, string filePath)
	{
		using SafeFileHandle handle = OpenExistingNoFollow(root, 131200u, 7u, expectDirectory: true);
		ValidateOpenedHandlePathAndType(handle, root, expectDirectory: true);
		(string DaclSddl, string OwnerSid) tuple = ReadHandleDaclSddl(handle, root);
		string item = tuple.DaclSddl;
		string item2 = tuple.OwnerSid;
		HashSet<string> hashSet = ParseExactFullControlSids(item, "OICI");
		string administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
		string system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
		if (hashSet == null || hashSet.Count != 3 || !hashSet.Contains(administrators) || !hashSet.Contains(system) || !string.Equals(item2, administrators, StringComparison.OrdinalIgnoreCase) || hashSet.Count((string sid) => !string.Equals(sid, administrators, StringComparison.OrdinalIgnoreCase) && !string.Equals(sid, system, StringComparison.OrdinalIgnoreCase)) != 1)
		{
			throw new UnauthorizedAccessException("DataRoot 句柄不是受保护的运行账户/Administrators/SYSTEM 精确 ACL。");
		}
		string path = Path.GetDirectoryName(filePath) ?? throw new InvalidDataException("运行文件父目录无效。");
		string relativePath = Path.GetRelativePath(root, path);
		if (relativePath == ".")
		{
			return hashSet;
		}
		if (relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relativePath == "..")
		{
			throw new UnauthorizedAccessException("运行文件父目录逃逸 DataRoot。");
		}
		string text = root;
		string[] array = relativePath.Split(new char[2]
		{
			Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar
		}, StringSplitOptions.RemoveEmptyEntries);
		foreach (string text2 in array)
		{
			if ((text2 == "." || text2 == "..") ? true : false)
			{
				throw new UnauthorizedAccessException("运行文件祖先目录包含无效路径段。");
			}
			text = Path.Combine(text, text2);
			using SafeFileHandle handle2 = OpenExistingNoFollow(text, 131200u, 7u, expectDirectory: true);
			ValidateOpenedHandlePathAndType(handle2, text, expectDirectory: true);
			var (sddl, item3) = ReadHandleDaclSddl(handle2, text);
			if (!hashSet.Contains(item3) || (!IsExactProtectedDacl(sddl, hashSet, "OICI") && !IsExactInheritedDirectoryDacl(sddl, hashSet)))
			{
				throw new UnauthorizedAccessException("运行文件祖先目录的句柄 owner/DACL 无法证明安全：" + text);
			}
		}
		return hashSet;
	}

	private static SafeFileHandle OpenExistingNoFollow(string path, uint desiredAccess, uint shareMode, bool expectDirectory)
	{
		uint flagsAndAttributes = (uint)(0x200000 | (expectDirectory ? 33554432 : 0));
		SafeFileHandle safeFileHandle = CreateFile(path, desiredAccess, shareMode, IntPtr.Zero, 3u, flagsAndAttributes, IntPtr.Zero);
		if (!safeFileHandle.IsInvalid)
		{
			return safeFileHandle;
		}
		int lastWin32Error = Marshal.GetLastWin32Error();
		safeFileHandle.Dispose();
		if ((uint)(lastWin32Error - 2) <= 1u)
		{
			throw new FileNotFoundException("受保护运行对象不存在。", path);
		}
		if (lastWin32Error == 5)
		{
			throw new UnauthorizedAccessException("无法安全打开受保护运行对象：" + path);
		}
		throw new Win32Exception(lastWin32Error, "无法安全打开受保护运行对象：" + path);
	}

	private static void ValidateOpenedHandlePathAndType(SafeFileHandle handle, string expectedPath, bool expectDirectory)
	{
		if (!GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileAttributeTagInfo, out var fileInformation, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取运行对象句柄属性。");
		}
		bool num = (fileInformation.FileAttributes & 0x400) != 0;
		bool flag = (fileInformation.FileAttributes & 0x10) != 0;
		if (num)
		{
			throw new IOException("拒绝访问重解析点句柄：" + expectedPath);
		}
		if (flag != expectDirectory)
		{
			throw new IOException(expectDirectory ? ("运行对象祖先不是目录：" + expectedPath) : ("运行文件路径被目录占用：" + expectedPath));
		}
		string finalDosPath = GetFinalDosPath(handle);
		string text = Path.GetFullPath(expectedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (!string.Equals(finalDosPath, text, StringComparison.OrdinalIgnoreCase))
		{
			throw new IOException("运行对象句柄最终路径不匹配，可能经过重解析点：expected=" + text + " actual=" + finalDosPath);
		}
	}

	private static string GetFinalDosPath(SafeFileHandle handle)
	{
		int num = 512;
		while (num <= 32768)
		{
			StringBuilder stringBuilder = new StringBuilder(num);
			uint finalPathNameByHandle = GetFinalPathNameByHandle(handle, stringBuilder, (uint)stringBuilder.Capacity, 0u);
			if (finalPathNameByHandle == 0)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "无法解析运行对象句柄最终路径。");
			}
			if (finalPathNameByHandle < stringBuilder.Capacity)
			{
				string text = stringBuilder.ToString();
				if (text.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
				{
					string text2 = text;
					text = "\\\\" + text2.Substring(8, text2.Length - 8);
				}
				else if (text.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
				{
					string text2 = text;
					text = text2.Substring(4, text2.Length - 4);
				}
				return Path.GetFullPath(text).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			}
			num = checked((int)finalPathNameByHandle + 1);
		}
		throw new IOException("运行对象句柄最终路径长度异常。");
	}

	private static (string DaclSddl, string OwnerSid) ReadHandleDaclSddl(SafeFileHandle handle, string path)
	{
		nint securityDescriptor = IntPtr.Zero;
		try
		{
			nint owner;
			nint group;
			nint dacl;
			nint sacl;
			uint securityInfo = GetSecurityInfo(handle.DangerousGetHandle(), 1, 5u, out owner, out group, out dacl, out sacl, out securityDescriptor);
			if (securityInfo != 0 || securityDescriptor == IntPtr.Zero || owner == IntPtr.Zero)
			{
				throw new Win32Exception(checked((int)securityInfo), "无法读取运行对象句柄安全描述符：" + path);
			}
			if (!ConvertSidToStringSid(owner, out var stringSid))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取运行对象句柄 owner：" + path);
			}
			string text;
			try
			{
				text = Marshal.PtrToStringUni(stringSid) ?? "";
			}
			finally
			{
				LocalFree(stringSid);
			}
			if (text.Length == 0)
			{
				throw new UnauthorizedAccessException("运行对象句柄 owner 为空：" + path);
			}
			if (!ConvertSecurityDescriptorToStringSecurityDescriptor(securityDescriptor, 1u, 4u, out var stringSecurityDescriptor, out var _))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取运行对象句柄 DACL：" + path);
			}
			try
			{
				string? obj = Marshal.PtrToStringUni(stringSecurityDescriptor) ?? "";
				if (obj.Length == 0)
				{
					throw new UnauthorizedAccessException("运行对象句柄 DACL 为空：" + path);
				}
				return (DaclSddl: obj, OwnerSid: text);
			}
			finally
			{
				LocalFree(stringSecurityDescriptor);
			}
		}
		finally
		{
			if (securityDescriptor != IntPtr.Zero)
			{
				LocalFree(securityDescriptor);
			}
		}
	}

	private static bool IsExactInheritedDirectoryDacl(string sddl, IReadOnlySet<string> allowedSids)
	{
		if (!sddl.StartsWith("D:AI", StringComparison.Ordinal))
		{
			return false;
		}
		MatchCollection matchCollection = Regex.Matches(sddl, "\\((?<type>[^;]*);(?<flags>[^;]*);(?<rights>[^;]*);[^;]*;[^;]*;(?<sid>[^)]*)\\)");
		if (matchCollection.Count != allowedSids.Count)
		{
			return false;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (Match item2 in matchCollection)
		{
			if (item2.Groups["type"].Value != "A" || item2.Groups["flags"].Value != "OICIID" || item2.Groups["rights"].Value != "FA")
			{
				return false;
			}
			string item;
			try
			{
				string value = item2.Groups["sid"].Value;
				string text = ((value == "BA") ? new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value : ((!(value == "SY")) ? new SecurityIdentifier(value).Value : new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value));
				item = text;
			}
			catch
			{
				return false;
			}
			if (!allowedSids.Contains(item) || !hashSet.Add(item))
			{
				return false;
			}
		}
		return allowedSids.All(hashSet.Contains);
	}

	public static void ValidateRuntimeSecretsForCurrentUser()
	{
		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException("运行时 ACL 校验仅支持 Windows。");
		}
		SecurityIdentifier securityIdentifier = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("无法读取当前运行账户 SID。");
		HashSet<string> allowedSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			securityIdentifier.Value,
			new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
			new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value
		};
		ValidateExistingProtectedObject("C:\\ProgramData\\P2PAgent", allowedSids, "OICI");
		foreach (string item in new string[5]
		{
			AppPaths.ConfigFile,
			AppPaths.SwarmKeyFile,
			AppPaths.IdentityFile,
			AppPaths.ApiTokenFile,
			AppPaths.JournalFile
		}.Where(File.Exists))
		{
			RejectReparsePoint(item);
			ValidateExistingProtectedObject(item, allowedSids, "", allowExactInheritance: true);
			RejectReparsePoint(item);
		}
	}

	public static void ValidateRuntimeProvisioningStartBoundary()
	{
		bool num = File.Exists(AppPaths.IdentityFile);
		bool flag = HasValidIdentityProvisioningMarker();
		bool flag2 = File.Exists(AppPaths.ApiTokenFile);
		bool flag3 = HasValidTokenProvisioningMarker();
		if (num)
		{
			using FileStream fileStream = OpenProtectedRuntimeFileForRead(AppPaths.IdentityFile);
			if (fileStream.Length == 0L)
			{
				throw new InvalidDataException("agent-identity.key 为空；拒绝启动以避免静默更换 PeerID。");
			}
			if (flag)
			{
				throw new InvalidDataException("检测到已生成 identity 与首次身份许可同时存在；请执行修复清理残留许可后再启动。");
			}
		}
		else if (!flag)
		{
			throw new FileNotFoundException("agent-identity.key 缺失；已安装 Worker 不会静默生成新 PeerID。请恢复身份备份，或通过明确的新设备安装流程重新注册。", AppPaths.IdentityFile);
		}
		if (flag2)
		{
			using (FileStream fileStream2 = OpenProtectedRuntimeFileForRead(AppPaths.ApiTokenFile))
			{
				if (fileStream2.Length == 0L)
				{
					throw new InvalidDataException("agent-api.token 为空；拒绝启动以避免静默轮换本机 API 凭据。");
				}
				if (flag3)
				{
					throw new InvalidDataException("检测到有效 API Token 与 Token 轮换许可同时存在；请执行修复清理残留许可后再启动。");
				}
				return;
			}
		}
		if (!flag && !flag3)
		{
			throw new FileNotFoundException("agent-api.token 缺失；已安装 Worker 不会在普通启动时静默轮换本机 API 凭据。请执行修复安装。", AppPaths.ApiTokenFile);
		}
	}

	public static bool HasValidIdentityProvisioningMarker()
	{
		return HasValidProvisioningMarker(AppPaths.IdentityProvisioningMarker, "ZhanClawControl identity provisioning v1\n", "首次身份许可");
	}

	public static bool HasValidTokenProvisioningMarker()
	{
		return HasValidProvisioningMarker(AppPaths.TokenProvisioningMarker, "ZhanClawControl api token provisioning v1\n", "API Token 轮换许可");
	}

	public static bool TryReadTaskMaintenanceMarker(out bool desiredEnabled)
	{
		return TryReadTaskMaintenanceMarker(out desiredEnabled, out var _);
	}

	public static bool TryReadTaskMaintenanceMarker(out bool desiredEnabled, out TaskMaintenancePhase phase)
	{
		desiredEnabled = false;
		phase = TaskMaintenancePhase.Mutation;
		if (!File.Exists(AppPaths.TaskMaintenanceMarker))
		{
			if (Directory.Exists(AppPaths.TaskMaintenanceMarker))
			{
				throw new InvalidDataException("计划任务维护意图路径被目录占用。 ");
			}
			return false;
		}
		(bool DesiredEnabled, TaskMaintenancePhase Phase) marker =
			ReadTaskMaintenanceMarkerAtPath(AppPaths.TaskMaintenanceMarker);
		desiredEnabled = marker.DesiredEnabled;
		phase = marker.Phase;
		return true;
	}

	public static void MarkTaskMaintenanceValidationReady()
	{
		if (!TryReadTaskMaintenanceMarker(out bool desiredEnabled, out TaskMaintenancePhase phase))
		{
			throw new InvalidDataException("受控验证启动缺少计划任务维护意图。 ");
		}
		if (phase != TaskMaintenancePhase.Mutation)
		{
			throw new InvalidDataException("计划任务维护意图不是 Mutation 阶段，拒绝重复放宽 Agent 执行边界。 ");
		}
		PublishTaskMaintenanceMarker(desiredEnabled, TaskMaintenancePhase.ValidationReady);
	}

	public static void RestoreTaskMaintenanceMutationPhaseIfPresent()
	{
		if (!TryReadTaskMaintenanceMarker(out bool desiredEnabled, out var _))
		{
			return;
		}
		// Always rewrite to the canonical v2 Mutation encoding. This safely
		// upgrades a legacy v1 marker after Agent execution has been restricted.
		PublishTaskMaintenanceMarker(desiredEnabled, TaskMaintenancePhase.Mutation);
	}

	public static void RetireTaskMaintenanceMarker()
	{
		CleanupTaskMaintenanceMarkerTombstone();
		if (Directory.Exists(AppPaths.TaskMaintenanceMarker))
		{
			throw new InvalidDataException("计划任务维护意图路径被目录占用。 ");
		}
		if (!File.Exists(AppPaths.TaskMaintenanceMarker))
		{
			return;
		}
		_ = ReadTaskMaintenanceMarkerAtPath(AppPaths.TaskMaintenanceMarker);
		if (!MoveFileEx(AppPaths.TaskMaintenanceMarker, AppPaths.TaskMaintenanceCleanupMarker, 8u))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "无法持久退休计划任务维护意图。 ");
		}
		if (File.Exists(AppPaths.TaskMaintenanceMarker) || Directory.Exists(AppPaths.TaskMaintenanceMarker))
		{
			throw new IOException("计划任务维护意图退休后活动路径仍存在。 ");
		}
		try
		{
			CleanupTaskMaintenanceMarkerTombstone();
		}
		catch
		{
		}
	}

	public static void CleanupTaskMaintenanceMarkerTombstone()
	{
		if (Directory.Exists(AppPaths.TaskMaintenanceCleanupMarker))
		{
			throw new InvalidDataException("计划任务维护清理墓碑路径被目录占用。 ");
		}
		if (File.Exists(AppPaths.TaskMaintenanceCleanupMarker))
		{
			_ = ReadTaskMaintenanceMarkerAtPath(AppPaths.TaskMaintenanceCleanupMarker);
			RejectReparsePoint(AppPaths.TaskMaintenanceCleanupMarker);
			File.Delete(AppPaths.TaskMaintenanceCleanupMarker);
		}
	}

	private static (bool DesiredEnabled, TaskMaintenancePhase Phase) ReadTaskMaintenanceMarkerAtPath(string path)
	{
		byte[] legacyEnabled = Encoding.UTF8.GetBytes(AppPaths.LegacyTaskMaintenanceEnabledContent);
		byte[] legacyDisabled = Encoding.UTF8.GetBytes(AppPaths.LegacyTaskMaintenanceDisabledContent);
		byte[] mutationEnabled = Encoding.UTF8.GetBytes(AppPaths.TaskMaintenanceMutationEnabledContent);
		byte[] mutationDisabled = Encoding.UTF8.GetBytes(AppPaths.TaskMaintenanceMutationDisabledContent);
		byte[] validationEnabled = Encoding.UTF8.GetBytes(AppPaths.TaskMaintenanceValidationReadyEnabledContent);
		byte[] validationDisabled = Encoding.UTF8.GetBytes(AppPaths.TaskMaintenanceValidationReadyDisabledContent);
		int num = new[]
		{
			legacyEnabled.Length, legacyDisabled.Length,
			mutationEnabled.Length, mutationDisabled.Length,
			validationEnabled.Length, validationDisabled.Length
		}.Max();
		ValidateInstallRootMarkerPath(path, "计划任务维护意图");
		using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, num, FileOptions.SequentialScan);
		if (fileStream.Length <= 0 || fileStream.Length > num)
		{
			throw new InvalidDataException("计划任务维护意图内容无效。");
		}
		byte[] array = new byte[checked((int)fileStream.Length)];
		fileStream.ReadExactly(array);
		RejectReparsePoint(path);
		if (array.AsSpan().SequenceEqual(legacyEnabled) ||
			array.AsSpan().SequenceEqual(mutationEnabled))
		{
			return (true, TaskMaintenancePhase.Mutation);
		}
		if (array.AsSpan().SequenceEqual(legacyDisabled) ||
			array.AsSpan().SequenceEqual(mutationDisabled))
		{
			return (false, TaskMaintenancePhase.Mutation);
		}
		if (array.AsSpan().SequenceEqual(validationEnabled))
		{
			return (true, TaskMaintenancePhase.ValidationReady);
		}
		if (array.AsSpan().SequenceEqual(validationDisabled))
		{
			return (false, TaskMaintenancePhase.ValidationReady);
		}
		throw new InvalidDataException("计划任务维护意图内容无效。");
	}

	private static void PublishTaskMaintenanceMarker(bool desiredEnabled, TaskMaintenancePhase phase)
	{
		EnsureSafeInstallRoot();
		CleanupTaskMaintenanceMarkerTombstone();
		if (Directory.Exists(AppPaths.TaskMaintenanceMarker))
		{
			throw new InvalidDataException("计划任务维护意图路径被目录占用。 ");
		}
		if (File.Exists(AppPaths.TaskMaintenanceMarker))
		{
			_ = ReadTaskMaintenanceMarkerAtPath(AppPaths.TaskMaintenanceMarker);
		}

		string content = phase switch
		{
			TaskMaintenancePhase.Mutation => desiredEnabled
				? AppPaths.TaskMaintenanceMutationEnabledContent
				: AppPaths.TaskMaintenanceMutationDisabledContent,
			TaskMaintenancePhase.ValidationReady => desiredEnabled
				? AppPaths.TaskMaintenanceValidationReadyEnabledContent
				: AppPaths.TaskMaintenanceValidationReadyDisabledContent,
			_ => throw new InvalidDataException("未知计划任务维护阶段。 ")
		};
		byte[] bytes = new UTF8Encoding(false, true).GetBytes(content);
		string stage = Path.Combine(AppPaths.InstallRoot,
			$".task-maintenance-{Guid.NewGuid():N}.stage");
		try
		{
			using (FileStream stream = new FileStream(stage, FileMode.CreateNew,
				FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
			{
				stream.Write(bytes);
				stream.Flush(flushToDisk: true);
			}
			RejectReparsePoint(stage);
			if (!MoveFileEx(stage, AppPaths.TaskMaintenanceMarker, 9u))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(),
					"无法持久更新计划任务维护阶段。 ");
			}
			(bool DesiredEnabled, TaskMaintenancePhase Phase) readBack =
				ReadTaskMaintenanceMarkerAtPath(AppPaths.TaskMaintenanceMarker);
			if (readBack.DesiredEnabled != desiredEnabled || readBack.Phase != phase)
			{
				throw new InvalidDataException("计划任务维护阶段写入后读回不一致。 ");
			}
		}
		finally
		{
			try
			{
				if (File.Exists(stage))
				{
					RejectReparsePoint(stage);
					File.Delete(stage);
				}
			}
			catch
			{
			}
		}
	}

	public static MaintenanceStartAuthorization EnforceMaintenanceStartBoundaryForCurrentUser()
	{
		if (!HasMaintenanceArtifacts)
		{
			if (HasMaintenanceStartPermitObject)
			{
				throw new InvalidDataException("检测到没有维护事务关联的一次性启动许可；拒绝启动并要求部署检查清理。 ");
			}
			return new MaintenanceStartAuthorization(IsMaintenance: false, AllowTrustedRollbackPayload: false, null);
		}
		if (!TryReadTaskMaintenanceMarker(out var _, out TaskMaintenancePhase phase))
		{
			throw new InvalidDataException("活动维护事务缺少计划任务维护意图；拒绝 AgentHost 启动。 ");
		}
		if (phase != TaskMaintenancePhase.ValidationReady)
		{
			throw new InvalidDataException("活动维护事务尚未进入 ValidationReady 阶段；拒绝 AgentHost 启动。 ");
		}
		ParsedMaintenanceStartPermit parsedMaintenanceStartPermit = ReadValidateAndOptionallyConsumeMaintenanceStartPermit(WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("无法读取 AgentHost 当前用户 SID。 "), consume: true, enforceLifetime: true);
		return new MaintenanceStartAuthorization(IsMaintenance: true, parsedMaintenanceStartPermit.AllowTrustedRollbackPayload, parsedMaintenanceStartPermit.AgentSha256);
	}

	public static void CreateMaintenanceStartPermit(string runAsUser, string agentSha256, bool allowTrustedRollbackPayload)
	{
		if (!TryReadTaskMaintenanceMarker(out var _, out TaskMaintenancePhase phase))
		{
			throw new InvalidOperationException("没有活动计划任务维护意图，不能创建维护启动许可。 ");
		}
		if (phase != TaskMaintenancePhase.Mutation)
		{
			throw new InvalidOperationException("计划任务维护意图不是 Mutation 阶段，不能创建新的维护启动许可。 ");
		}
		SecurityIdentifier securityIdentifier = ResolveInteractiveUserSid(runAsUser);
		if (agentSha256.Length != 64 || agentSha256.Any((char value) => !Uri.IsHexDigit(value)))
		{
			throw new InvalidDataException("维护启动许可 Agent SHA-256 无效。 ");
		}
		EnsureSafeInstallRoot();
		DeleteMaintenanceStartPermitIfPresent(securityIdentifier.Value);
		string text = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
		string s = string.Join('\n', "ZhanClawControl maintenance start permit v1", "run_as_sid=" + securityIdentifier.Value, "created_utc_ticks=" + DateTimeOffset.UtcNow.UtcTicks, "payload_mode=" + (allowTrustedRollbackPayload ? "trusted-rollback" : "current"), "agent_sha256=" + agentSha256.ToUpperInvariant(), "nonce=" + text) + "\n";
		byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(s);
		string text2 = Path.Combine("C:\\Program Files\\P2PAgent", $".maintenance-start-permit-{Guid.NewGuid():N}.stage");
		try
		{
			using (FileStream fileStream = new FileStream(text2, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
			{
				fileStream.Write(bytes);
				fileStream.Flush(flushToDisk: true);
			}
			ApplyMaintenanceStartPermitDacl(text2, securityIdentifier);
			ReadValidateMaintenanceStartPermitAtPath(text2, securityIdentifier, enforceLifetime: true);
			if (!MoveFileEx(text2, AppPaths.MaintenanceStartPermit, 8u))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "无法持久发布一次性维护启动许可。 ");
			}
			ReadValidateAndOptionallyConsumeMaintenanceStartPermit(securityIdentifier, consume: false, enforceLifetime: true);
		}
		finally
		{
			try
			{
				if (File.Exists(text2))
				{
					File.Delete(text2);
				}
			}
			catch
			{
			}
		}
	}

	public static void DeleteMaintenanceStartPermitIfPresent(string? expectedRunAsUser = null)
	{
		if (Directory.Exists(AppPaths.MaintenanceStartPermit))
		{
			throw new InvalidDataException("一次性维护启动许可路径被目录占用。 ");
		}
		if (File.Exists(AppPaths.MaintenanceStartPermit))
		{
			SecurityIdentifier expectedSid = null;
			if (!string.IsNullOrWhiteSpace(expectedRunAsUser))
			{
				expectedSid = ResolveInteractiveUserSid(expectedRunAsUser);
			}
			ReadValidateAndOptionallyConsumeMaintenanceStartPermit(expectedSid, consume: true, enforceLifetime: false);
		}
	}

	public static string ValidateMaintenanceStartPermitForDeployment()
	{
		return ReadValidateAndOptionallyConsumeMaintenanceStartPermit(null, consume: false, enforceLifetime: true).RunAsSid.Value;
	}

	private static ParsedMaintenanceStartPermit ReadValidateAndOptionallyConsumeMaintenanceStartPermit(SecurityIdentifier? expectedSid, bool consume, bool enforceLifetime)
	{
		if (Directory.Exists(AppPaths.MaintenanceStartPermit))
		{
			throw new InvalidDataException("一次性维护启动许可路径被目录占用。 ");
		}
		if (!File.Exists(AppPaths.MaintenanceStartPermit))
		{
			throw new FileNotFoundException("活动维护事务缺少一次性启动许可。 ", AppPaths.MaintenanceStartPermit);
		}
		uint desiredAccess = (uint)(-2147352448 | (consume ? 65536 : 0));
		using SafeFileHandle safeFileHandle = OpenExistingNoFollow(AppPaths.MaintenanceStartPermit, desiredAccess, (!consume) ? 5u : 0u, expectDirectory: false);
		ParsedMaintenanceStartPermit result = ReadValidateMaintenanceStartPermitHandle(safeFileHandle, AppPaths.MaintenanceStartPermit, expectedSid, enforceLifetime);
		if (consume)
		{
			FileDispositionInfo fileInformation = new FileDispositionInfo
			{
				DeleteFile = true
			};
			if (!SetFileInformationByHandle(safeFileHandle, FileInfoByHandleClass.FileDispositionInfo, ref fileInformation, (uint)Marshal.SizeOf<FileDispositionInfo>()))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "无法原子消费一次性维护启动许可。 ");
			}
		}
		return result;
	}

	private static ParsedMaintenanceStartPermit ReadValidateMaintenanceStartPermitAtPath(string path, SecurityIdentifier expectedSid, bool enforceLifetime)
	{
		using SafeFileHandle handle = OpenExistingNoFollow(path, 2147614848u, 5u, expectDirectory: false);
		return ReadValidateMaintenanceStartPermitHandle(handle, path, expectedSid, enforceLifetime);
	}

	private static ParsedMaintenanceStartPermit ReadValidateMaintenanceStartPermitHandle(SafeFileHandle handle, string expectedPath, SecurityIdentifier? expectedSid, bool enforceLifetime)
	{
		ValidateInstallRootDirectFilePath(expectedPath, "一次性维护启动许可");
		ValidateOpenedHandlePathAndType(handle, expectedPath, expectDirectory: false);
		(string DaclSddl, string OwnerSid) tuple = ReadHandleDaclSddl(handle, expectedPath);
		string item = tuple.DaclSddl;
		string item2 = tuple.OwnerSid;
		string value = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
		if (!string.Equals(item2, value, StringComparison.OrdinalIgnoreCase))
		{
			throw new UnauthorizedAccessException("一次性维护启动许可 owner 不是 Builtin Administrators。 ");
		}
		long length = RandomAccess.GetLength(handle);
		if ((length <= 0 || length > 512) ? true : false)
		{
			throw new InvalidDataException("一次性维护启动许可长度无效。 ");
		}
		byte[] array = new byte[checked((int)length)];
		int num;
		for (int i = 0; i < array.Length; i += num)
		{
			num = RandomAccess.Read(handle, array.AsSpan(i), i);
			if (num == 0)
			{
				throw new IOException("一次性维护启动许可读取不完整。 ");
			}
		}
		string[] array2 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(array).Split('\n');
		if (array2.Length != 7 || array2[6].Length != 0 || array2[0] != "ZhanClawControl maintenance start permit v1" || !array2[1].StartsWith("run_as_sid=", StringComparison.Ordinal) || !array2[2].StartsWith("created_utc_ticks=", StringComparison.Ordinal) || !array2[3].StartsWith("payload_mode=", StringComparison.Ordinal) || !array2[4].StartsWith("agent_sha256=", StringComparison.Ordinal) || !array2[5].StartsWith("nonce=", StringComparison.Ordinal))
		{
			throw new InvalidDataException("一次性维护启动许可内容结构无效。 ");
		}
		SecurityIdentifier securityIdentifier;
		string text;
		int length2;
		try
		{
			text = array2[1];
			length2 = "run_as_sid=".Length;
			securityIdentifier = new SecurityIdentifier(text.Substring(length2, text.Length - length2));
		}
		catch (Exception innerException)
		{
			throw new InvalidDataException("维护启动许可 SID 无效。 ", innerException);
		}
		ResolveInteractiveUserSid(securityIdentifier.Value);
		if ((object)expectedSid != null && !securityIdentifier.Equals(expectedSid))
		{
			throw new UnauthorizedAccessException("维护启动许可与精确任务运行用户不匹配。 ");
		}
		text = array2[2];
		length2 = "created_utc_ticks=".Length;
		if (!long.TryParse(text.Substring(length2, text.Length - length2), NumberStyles.None, CultureInfo.InvariantCulture, out var result))
		{
			throw new InvalidDataException("维护启动许可时间戳无效。 ");
		}
		DateTimeOffset dateTimeOffset;
		try
		{
			dateTimeOffset = new DateTimeOffset(result, TimeSpan.Zero);
		}
		catch (ArgumentOutOfRangeException innerException2)
		{
			throw new InvalidDataException("维护启动许可时间戳超出范围。 ", innerException2);
		}
		text = array2[3];
		length2 = "payload_mode=".Length;
		string text2 = text.Substring(length2, text.Length - length2);
		if ((!(text2 == "current") && !(text2 == "trusted-rollback")) || 1 == 0)
		{
			throw new InvalidDataException("维护启动许可 payload mode 无效。 ");
		}
		text = array2[4];
		length2 = "agent_sha256=".Length;
		string text3 = text.Substring(length2, text.Length - length2);
		if (!Regex.IsMatch(text3, "^[0-9A-F]{64}$"))
		{
			throw new InvalidDataException("维护启动许可 Agent SHA-256 无效。 ");
		}
		text = array2[5];
		length2 = "nonce=".Length;
		if (!Regex.IsMatch(text.Substring(length2, text.Length - length2), "^[0-9A-F]{32}$"))
		{
			throw new InvalidDataException("维护启动许可 nonce 无效。 ");
		}
		TimeSpan timeSpan = DateTimeOffset.UtcNow - dateTimeOffset;
		if (enforceLifetime && (timeSpan < TimeSpan.FromSeconds(-10.0) || timeSpan > MaintenanceStartPermitLifetime))
		{
			throw new InvalidDataException("一次性维护启动许可已过期或时间来自未来。 ");
		}
		if (!IsExactMaintenanceStartPermitDacl(item, securityIdentifier))
		{
			throw new UnauthorizedAccessException("一次性维护启动许可 DACL 不是精确最小权限。 ");
		}
		return new ParsedMaintenanceStartPermit(securityIdentifier, text2 == "trusted-rollback", text3);
	}

	private static bool IsExactMaintenanceStartPermitDacl(string sddl, SecurityIdentifier runAsSid)
	{
		if (!sddl.StartsWith("D:P", StringComparison.Ordinal))
		{
			return false;
		}
		string value = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
		string value2 = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
		Dictionary<string, int> expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
		{
			[value] = 2032127,
			[value2] = 2032127,
			[runAsSid.Value] = 1245321
		};
		return HasExactFileAceSet(sddl, expected, "");
	}

	private static void ApplyMaintenanceStartPermitDacl(string path, SecurityIdentifier runAsSid)
	{
		string sddl = $"O:BAD:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;0x{1245321:X};;;{runAsSid.Value})";
		ApplyFileSecurityDescriptor(path, sddl, "无法设置一次性维护启动许可 DACL。 ");
	}

	public static void RestrictAgentExecutionForMaintenance(string path)
	{
		ValidateAgentInstallPath(path);
		if (File.Exists(path))
		{
			AgentExecutionAclState agentExecutionAclState = ReadAgentExecutionAclState(path);
			if ((uint)(agentExecutionAclState - 1) > 1u)
			{
				throw new UnauthorizedAccessException("Agent 执行 ACL 不可信，拒绝接管。 ");
			}
			if (agentExecutionAclState == AgentExecutionAclState.Normal)
			{
				ValidateTrustedAgentPublisherForRollback(path);
			}
			ApplyFileSecurityDescriptor(path, "O:BAD:P(A;;FA;;;BA)(A;;FA;;;SY)", "无法限制维护期间 Agent 执行权限。 ");
			if (ReadAgentExecutionAclState(path) != AgentExecutionAclState.Restricted)
			{
				throw new UnauthorizedAccessException("Agent 维护执行 ACL 写后复核失败。 ");
			}
			ValidateTrustedAgentPublisherForRollback(path);
		}
	}

	public static void RestoreAgentExecutionForControlledStart(string path)
	{
		ValidateAgentInstallPath(path);
		if (!File.Exists(path))
		{
			throw new FileNotFoundException("Agent 执行文件不存在。 ", path);
		}
		AgentExecutionAclState agentExecutionAclState = ReadAgentExecutionAclState(path);
		if ((uint)(agentExecutionAclState - 1) > 1u)
		{
			throw new UnauthorizedAccessException("Agent 执行 ACL 不可信，拒绝放宽。 ");
		}
		ApplyFileSecurityDescriptor(path, "O:BAD:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;0x1200A9;;;BU)", "无法恢复 Agent 正常只读执行 ACL。 ");
		if (ReadAgentExecutionAclState(path) != AgentExecutionAclState.Normal)
		{
			throw new UnauthorizedAccessException("Agent 正常执行 ACL 写后复核失败。 ");
		}
	}

	public static bool IsAgentExecutionRestricted(string path)
	{
		ValidateAgentInstallPath(path);
		if (!File.Exists(path))
		{
			return false;
		}
		return ReadAgentExecutionAclState(path) switch
		{
			AgentExecutionAclState.Restricted => true, 
			AgentExecutionAclState.Normal => false, 
			_ => throw new UnauthorizedAccessException("Agent 执行 ACL 既非正常态也非维护限制态。 "), 
		};
	}

	private static AgentExecutionAclState ReadAgentExecutionAclState(string path)
	{
		using SafeFileHandle handle = OpenExistingNoFollow(path, 131200u, 7u, expectDirectory: false);
		ValidateOpenedHandlePathAndType(handle, path, expectDirectory: false);
		(string DaclSddl, string OwnerSid) tuple = ReadHandleDaclSddl(handle, path);
		string item = tuple.DaclSddl;
		string item2 = tuple.OwnerSid;
		string value = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
		string value2 = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
		string value3 = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value;
		if (string.Equals(item2, value, StringComparison.OrdinalIgnoreCase) && HasExactFileAceSet(item, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
		{
			[value] = 2032127,
			[value2] = 2032127
		}, ""))
		{
			return AgentExecutionAclState.Restricted;
		}
		Dictionary<string, int> expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
		{
			[value] = 2032127,
			[value2] = 2032127,
			[value3] = 1179817
		};
		bool flag = string.Equals(item2, value, StringComparison.OrdinalIgnoreCase) && HasExactFileAceSet(item, expected, "");
		bool flag2 = IsLocalAdministratorUserSid(item2) && HasExactFileAceSet(item, expected, "ID");
		return (flag || flag2) ? AgentExecutionAclState.Normal : AgentExecutionAclState.Invalid;
	}

	private static bool HasExactFileAceSet(string sddl, IReadOnlyDictionary<string, int> expected, string expectedFlags)
	{
		if (!((expectedFlags.Length == 0) ? sddl.StartsWith("D:P", StringComparison.Ordinal) : sddl.StartsWith("D:AI", StringComparison.Ordinal)))
		{
			return false;
		}
		MatchCollection matchCollection = Regex.Matches(sddl, "\\((?<type>[^;]*);(?<flags>[^;]*);(?<rights>[^;]*);[^;]*;[^;]*;(?<sid>[^)]*)\\)");
		if (matchCollection.Count != expected.Count)
		{
			return false;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (Match item in matchCollection)
		{
			if (item.Groups["type"].Value != "A" || item.Groups["flags"].Value != expectedFlags)
			{
				return false;
			}
			string text;
			try
			{
				string value = item.Groups["sid"].Value;
				text = value switch
				{
					"BA" => new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value, 
					"SY" => new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value, 
					"BU" => new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value, 
					_ => new SecurityIdentifier(value).Value, 
				};
			}
			catch
			{
				return false;
			}
			if (!expected.TryGetValue(text, out var value2) || !hashSet.Add(text))
			{
				return false;
			}
			string value3 = item.Groups["rights"].Value;
			int num;
			if (value3 == "FA")
			{
				num = 2032127;
			}
			else
			{
				if (!value3.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || !int.TryParse(value3.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var result))
				{
					return false;
				}
				num = result;
			}
			if (num != value2)
			{
				return false;
			}
		}
		return hashSet.Count == expected.Count;
	}

	private static bool IsLocalAdministratorUserSid(string sidValue)
	{
		SecurityIdentifier securityIdentifier;
		try
		{
			securityIdentifier = new SecurityIdentifier(sidValue);
		}
		catch
		{
			return false;
		}
		SecurityIdentifier sid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
		if (securityIdentifier.Equals(sid))
		{
			return true;
		}
		if (securityIdentifier.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)))
		{
			return true;
		}
		using (WindowsIdentity windowsIdentity = WindowsIdentity.GetCurrent())
		{
			if ((object)windowsIdentity.User != null && securityIdentifier.Equals(windowsIdentity.User) && new WindowsPrincipal(windowsIdentity).IsInRole(WindowsBuiltInRole.Administrator))
			{
				return true;
			}
		}
		(string Name, string Domain, SidNameUse Use) tuple = LookupSidAccount(securityIdentifier);
		var (text, text2, _) = tuple;
		if (tuple.Use != SidNameUse.User || text.Length == 0)
		{
			return false;
		}
		(string Name, string Domain, SidNameUse Use) tuple3 = LookupSidAccount(sid);
		string item = tuple3.Name;
		SidNameUse item2 = tuple3.Use;
		bool flag = (uint)(item2 - 4) <= 1u;
		if (!flag || item.Length == 0)
		{
			return false;
		}
		foreach (string item3 in new string[2]
		{
			(text2.Length == 0) ? text : (text2 + "\\" + text),
			text
		}.Distinct<string>(StringComparer.OrdinalIgnoreCase))
		{
			nint buffer = IntPtr.Zero;
			try
			{
				if (NetUserGetLocalGroups(null, item3, 0, 1, out buffer, -1, out var entriesRead, out var _) != 0)
				{
					continue;
				}
				int num = Marshal.SizeOf<LocalGroupUsersInfo0>();
				for (int i = 0; i < entriesRead; i++)
				{
					if (string.Equals(Marshal.PtrToStringUni(Marshal.PtrToStructure<LocalGroupUsersInfo0>(IntPtr.Add(buffer, checked(i * num))).Name) ?? "", item, StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}
			}
			finally
			{
				if (buffer != IntPtr.Zero)
				{
					NetApiBufferFree(buffer);
				}
			}
		}
		return false;
	}

	private static (string Name, string Domain, SidNameUse Use) LookupSidAccount(SecurityIdentifier sid)
	{
		byte[] array = new byte[sid.BinaryLength];
		sid.GetBinaryForm(array, 0);
		uint nameLength = 0u;
		uint referencedDomainNameLength = 0u;
		LookupAccountSid(null, array, null, ref nameLength, null, ref referencedDomainNameLength, out var use);
		if (Marshal.GetLastWin32Error() != 122 || nameLength == 0)
		{
			return (Name: "", Domain: "", Use: use);
		}
		checked
		{
			StringBuilder stringBuilder = new StringBuilder((int)nameLength);
			StringBuilder stringBuilder2 = ((referencedDomainNameLength == 0) ? null : new StringBuilder((int)referencedDomainNameLength));
			if (!LookupAccountSid(null, array, stringBuilder, ref nameLength, stringBuilder2, ref referencedDomainNameLength, out use))
			{
				return (Name: "", Domain: "", Use: use);
			}
			return (Name: stringBuilder.ToString(), Domain: stringBuilder2?.ToString() ?? "", Use: use);
		}
	}

	private static void ValidateAgentInstallPath(string path)
	{
		string path2 = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string text = Path.GetFullPath("C:\\Program Files\\P2PAgent").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (!string.Equals(Path.GetDirectoryName(path2), text, StringComparison.OrdinalIgnoreCase))
		{
			throw new UnauthorizedAccessException("Agent 执行对象必须是 InstallRoot 的直接文件。 ");
		}
		string fileName = Path.GetFileName(path2);
		if (!string.Equals(fileName, "p2p-agent.exe", StringComparison.OrdinalIgnoreCase) && !fileName.StartsWith("p2p-agent.exe.", StringComparison.OrdinalIgnoreCase) && !fileName.StartsWith(".p2p-agent.exe.", StringComparison.OrdinalIgnoreCase))
		{
			throw new UnauthorizedAccessException("Agent 执行对象文件名不在受控集合中。 ");
		}
		RejectReparsePoint(text);
		RejectReparsePoint(path2);
		if (Directory.Exists(path2))
		{
			throw new IOException("Agent 执行路径被目录占用。 ");
		}
	}

	private static void ValidateInstallRootDirectFilePath(string path, string description)
	{
		string path2 = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string text = Path.GetFullPath("C:\\Program Files\\P2PAgent").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (!string.Equals(Path.GetDirectoryName(path2), text, StringComparison.OrdinalIgnoreCase))
		{
			throw new UnauthorizedAccessException(description + "必须是 InstallRoot 的直接文件。 ");
		}
		RejectReparsePoint(text);
		RejectReparsePoint(path2);
		if (Directory.Exists(path2))
		{
			throw new IOException(description + "路径被目录占用。 ");
		}
	}

	private static void ApplyFileSecurityDescriptor(string path, string sddl, string errorMessage)
	{
		ValidateInstallRootDirectFilePath(path, "受保护程序文件");
		if (!File.Exists(path))
		{
			throw new FileNotFoundException(errorMessage, path);
		}
		if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1u, out var securityDescriptor, out var _))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), errorMessage);
		}
		try
		{
			ApplyNamedSecurityDescriptor(path, securityDescriptor, errorMessage);
		}
		finally
		{
			LocalFree(securityDescriptor);
		}
		RejectReparsePoint(path);
	}

	private static bool HasValidProvisioningMarker(string markerPath, string markerContent, string description)
	{
		if (!File.Exists(markerPath))
		{
			return false;
		}
		ValidateInstallRootMarkerPath(markerPath, description);
		byte[] bytes = Encoding.UTF8.GetBytes(markerContent);
		using FileStream fileStream = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read, bytes.Length, FileOptions.SequentialScan);
		if (fileStream.Length != bytes.Length)
		{
			throw new InvalidDataException(description + "内容无效。");
		}
		byte[] array = new byte[bytes.Length];
		fileStream.ReadExactly(array);
		if (!array.AsSpan().SequenceEqual(bytes))
		{
			throw new InvalidDataException(description + "内容无效。");
		}
		RejectReparsePoint(markerPath);
		return true;
	}

	private static void ValidateInstallRootMarkerPath(string markerPath, string description)
	{
		string text = Path.GetFullPath("C:\\Program Files\\P2PAgent").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string fullPath = Path.GetFullPath(markerPath);
		if (!string.Equals(Path.GetDirectoryName(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), text, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException(description + "不在固定 InstallRoot 中。");
		}
		RejectReparsePoint(text);
		RejectReparsePoint(fullPath);
	}

	private static HashSet<string>? ParseExactFullControlSids(string sddl, string expectedFlags)
	{
		if (!sddl.StartsWith("D:P", StringComparison.Ordinal))
		{
			return null;
		}
		MatchCollection matchCollection = Regex.Matches(sddl, "\\((?<type>[^;]*);(?<flags>[^;]*);(?<rights>[^;]*);[^;]*;[^;]*;(?<sid>[^)]*)\\)");
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (Match item2 in matchCollection)
		{
			if (item2.Groups["type"].Value != "A" || item2.Groups["rights"].Value != "FA" || item2.Groups["flags"].Value != expectedFlags)
			{
				return null;
			}
			string item;
			try
			{
				string value = item2.Groups["sid"].Value;
				string text = ((value == "BA") ? new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value : ((!(value == "SY")) ? new SecurityIdentifier(value).Value : new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value));
				item = text;
			}
			catch
			{
				return null;
			}
			if (!hashSet.Add(item))
			{
				return null;
			}
		}
		if (hashSet.Count != matchCollection.Count)
		{
			return null;
		}
		return hashSet;
	}

	public static void ValidateSwarmKey(string path)
	{
		RejectReparsePoint(path);
		string[] array = File.ReadAllLines(path);
		if (array.Length != 3 || array[0] != "/key/swarm/psk/1.0.0/" || array[1] != "/base16/" || array[2].Length != 64 || array[2].Any((char c) => !Uri.IsHexDigit(c)))
		{
			throw new InvalidDataException("swarm.key 格式无效；期望标准 libp2p pnet base16 三行格式。");
		}
	}

	public static async Task ValidateAgentPayloadAsync(string path, CancellationToken ct = default(CancellationToken))
	{
		RejectReparsePoint(path);
		PayloadManifest manifest = LoadPayloadManifest();
		if (!string.Equals(Path.GetFileName(path), manifest.FileName, StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(path).StartsWith(manifest.FileName + ".", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("Agent payload 文件名异常：" + Path.GetFileName(path));
		}
		await using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			string text = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(continueOnCapturedContext: false)).ToLowerInvariant();
			if (!string.Equals(text, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("p2p-agent.exe SHA-256 不匹配。expected=" + manifest.Sha256 + " actual=" + text);
			}
		}
		ValidatePe(path);
		if (manifest.RequireAuthenticodeValid)
		{
			ValidateAuthenticode(path, manifest.ExpectedSignerCommonName, manifest.ExpectedLeafCertificateSha256, manifest.ExpectedSpkiSha256);
		}
	}

	public static void ValidateTrustedAgentPublisher(string path)
	{
		RejectReparsePoint(path);
		PayloadManifest payloadManifest = LoadPayloadManifest();
		ValidatePe(path);
		if (!payloadManifest.RequireAuthenticodeValid)
		{
			throw new InvalidDataException("payload manifest 未要求 Authenticode，不能用于旧版 Agent 回滚信任。");
		}
		ValidateAuthenticode(path, payloadManifest.ExpectedSignerCommonName, payloadManifest.ExpectedLeafCertificateSha256, payloadManifest.ExpectedSpkiSha256);
	}

	public static void ValidateTrustedAgentPublisherForRollback(string path)
	{
		RejectReparsePoint(path);
		PayloadManifest payloadManifest = LoadPayloadManifest();
		ValidatePe(path);
		ValidateAuthenticodePublisher(path, payloadManifest.ExpectedSignerCommonName, payloadManifest.TrustedRollbackSpkiSha256);
	}

	private static void ValidateAuthenticodePublisher(string path, string expectedSignerCommonName, IReadOnlyCollection<string> trustedSpkiPins)
	{
		ValidateAuthenticode(path, expectedSignerCommonName, null, null, trustedSpkiPins);
	}

	private static void ValidatePe(string path)
	{
		using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		using BinaryReader binaryReader = new BinaryReader(fileStream);
		if (binaryReader.ReadUInt16() != 23117)
		{
			throw new InvalidDataException("p2p-agent.exe 缺少 MZ 头。");
		}
		fileStream.Position = 60L;
		int num = binaryReader.ReadInt32();
		if (num < 64 || num > fileStream.Length - 94)
		{
			throw new InvalidDataException("p2p-agent.exe PE 头偏移无效。");
		}
		fileStream.Position = num;
		if (binaryReader.ReadUInt32() != 17744)
		{
			throw new InvalidDataException("p2p-agent.exe 缺少 PE 签名。");
		}
		if (binaryReader.ReadUInt16() != 34404)
		{
			throw new InvalidDataException("p2p-agent.exe 不是 AMD64 PE。");
		}
		fileStream.Position = num + 24;
		if (binaryReader.ReadUInt16() != 523)
		{
			throw new InvalidDataException("p2p-agent.exe 不是 PE32+。");
		}
		fileStream.Position = num + 24 + 68;
		if (binaryReader.ReadUInt16() != 3)
		{
			throw new InvalidDataException("p2p-agent.exe 不是 Console 子系统程序。");
		}
	}

	private static PayloadManifest LoadPayloadManifest()
	{
		using Stream utf8Json = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZhanClawControl.payload.payload-manifest.json") ?? throw new FileNotFoundException("安装包缺少 payload-manifest.json。");
		PayloadManifest payloadManifest = JsonSerializer.Deserialize<PayloadManifest>(utf8Json, new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true
		}) ?? throw new InvalidDataException("payload-manifest.json 无法解析。");
		if (payloadManifest.SchemaVersion != 1 || payloadManifest.FileName != "p2p-agent.exe" || payloadManifest.Sha256.Length != 64 || payloadManifest.Sha256.Any((char c) => !Uri.IsHexDigit(c)) || string.IsNullOrWhiteSpace(payloadManifest.Version) || !string.Equals(payloadManifest.PeMachine, "amd64", StringComparison.Ordinal) || !string.Equals(payloadManifest.PeSubsystem, "console", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(payloadManifest.ExpectedSignerCommonName) || payloadManifest.ExpectedLeafCertificateSha256.Length != 64 || payloadManifest.ExpectedSpkiSha256.Length != 64)
		{
			throw new InvalidDataException("payload-manifest.json 内容无效。");
		}
		if (payloadManifest.TrustedRollbackSpkiSha256.Count == 0 || payloadManifest.TrustedRollbackSpkiSha256.Any((string pin) => pin.Length != 64 || pin.Any((char c) => !Uri.IsHexDigit(c))))
		{
			throw new InvalidDataException("payload-manifest.json 缺少有效 trusted rollback SPKI pins。");
		}
		return payloadManifest;
	}

	public static void RejectReparsePoint(string path)
	{
		if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != FileAttributes.None)
		{
			throw new IOException("拒绝访问重解析点：" + path);
		}
	}

	private static void ApplyExactDacl(string path, SecurityIdentifier userSid, bool isDirectory)
	{
		string value = (isDirectory ? "OICI" : "");
		if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"O:BAD:P(A;{value};FA;;;{userSid.Value})(A;{value};FA;;;BA)(A;{value};FA;;;SY)", 1u, out var securityDescriptor, out var _))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "无法生成安全描述符：" + path);
		}
		try
		{
			ApplyNamedSecurityDescriptor(path, securityDescriptor, "无法设置完整 DACL：" + path);
		}
		finally
		{
			LocalFree(securityDescriptor);
		}
	}

	private static void ApplyInstallRootDacl(string path)
	{
		if (!ConvertStringSecurityDescriptorToSecurityDescriptor("O:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;0x1200a9;;;BU)", 1u, out var securityDescriptor, out var _))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "无法生成程序目录安全描述符：" + path);
		}
		try
		{
			ApplyNamedSecurityDescriptor(path, securityDescriptor, "无法加固程序目录 ACL：" + path);
		}
		finally
		{
			LocalFree(securityDescriptor);
		}
	}

	public static void PrepareSecureRollbackDirectory(string path)
	{
		string fullPath = Path.GetFullPath(path);
		string value = Path.GetFullPath("C:\\Program Files\\P2PAgent").TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		if (!fullPath.StartsWith(value, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("回滚目录必须位于受保护的 InstallRoot 内。");
		}
		Directory.CreateDirectory(fullPath);
		RejectReparsePoint(fullPath);
		if (!ConvertStringSecurityDescriptorToSecurityDescriptor("O:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)", 1u, out var securityDescriptor, out var _))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "无法生成回滚目录安全描述符。");
		}
		try
		{
			ApplyNamedSecurityDescriptor(fullPath, securityDescriptor, "无法加固回滚目录 ACL。");
		}
		finally
		{
			LocalFree(securityDescriptor);
		}
		RejectReparsePoint(fullPath);
	}

	public static void ValidateProtectedRollbackDirectory(string path)
	{
		string text = ValidateRollbackPath(path);
		if (!Directory.Exists(text))
		{
			throw new DirectoryNotFoundException("受保护恢复目录不存在：" + text);
		}
		RejectReparsePoint(text);
		ValidateExistingProtectedObject(text, CreateRollbackAllowedSids(), "OICI");
		RejectReparsePoint(text);
	}

	public static string ReadProtectedRollbackTextFile(string protectedRoot, string path, int maxBytes)
	{
		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException("受保护恢复状态读取仅支持 Windows。");
		}
		if (maxBytes <= 0)
		{
			throw new ArgumentOutOfRangeException("maxBytes");
		}
		string text = ValidateRollbackPath(protectedRoot);
		string text2 = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (!string.Equals((Path.GetDirectoryName(text2) ?? throw new InvalidDataException("恢复状态文件缺少父目录。")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), text, StringComparison.OrdinalIgnoreCase))
		{
			throw new UnauthorizedAccessException("恢复状态文件必须是受保护恢复根目录的直接子项。");
		}
		HashSet<string> hashSet = CreateRollbackAllowedSids();
		using (SafeFileHandle handle = OpenExistingNoFollow(text, 131200u, 7u, expectDirectory: true))
		{
			ValidateOpenedHandlePathAndType(handle, text, expectDirectory: true);
			var (sddl, item) = ReadHandleDaclSddl(handle, text);
			if (!hashSet.Contains(item) || !IsExactProtectedDacl(sddl, hashSet, "OICI"))
			{
				throw new UnauthorizedAccessException("恢复根目录句柄不是精确 BA/SY-only 边界。");
			}
		}
		using SafeFileHandle handle2 = OpenExistingNoFollow(text2, 2147614848u, 5u, expectDirectory: false);
		ValidateOpenedHandlePathAndType(handle2, text2, expectDirectory: false);
		var (sddl2, item2) = ReadHandleDaclSddl(handle2, text2);
		if (!hashSet.Contains(item2) || !IsExactProtectedDacl(sddl2, hashSet, ""))
		{
			throw new UnauthorizedAccessException("恢复状态文件句柄不是精确 BA/SY-only 边界。");
		}
		using FileStream fileStream = new FileStream(handle2, FileAccess.Read, 4096, isAsync: false);
		if (fileStream.Length <= 0 || fileStream.Length > maxBytes)
		{
			throw new InvalidDataException($"恢复状态文件长度必须为 1–{maxBytes} 字节。");
		}
		byte[] array = new byte[checked((int)fileStream.Length)];
		fileStream.ReadExactly(array);
		return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(array);
	}

	public static void ProtectRollbackFile(string protectedRoot, string path)
	{
		string text = ValidateRollbackPath(protectedRoot);
		ValidateProtectedRollbackDirectory(text);
		string text2 = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (!string.Equals((Path.GetDirectoryName(text2) ?? throw new InvalidDataException("恢复文件缺少父目录。")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), text, StringComparison.OrdinalIgnoreCase))
		{
			throw new UnauthorizedAccessException("恢复文件必须是受保护恢复根目录的直接子项。");
		}
		if (!File.Exists(text2))
		{
			throw new FileNotFoundException("恢复文件不存在。", text2);
		}
		RejectReparsePoint(text2);
		ApplyRollbackDacl(text2, isDirectory: false);
		RejectReparsePoint(text2);
	}

	public static void ProtectMovedDataRootForQuarantine(string path, string runAsUser)
	{
		string text = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string b = Path.GetFullPath(AppPaths.UninstallRecoveryDataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (!string.Equals(text, b, StringComparison.OrdinalIgnoreCase))
		{
			throw new UnauthorizedAccessException("卸载数据隔离目录不是固定恢复路径。");
		}
		ValidateProtectedRollbackDirectory(AppPaths.UninstallRecoveryRoot);
		if (!Directory.Exists(text))
		{
			throw new DirectoryNotFoundException("卸载数据隔离目录不存在。");
		}
		RejectReparsePoint(text);
		HashSet<string> allowedSids = CreateRollbackAllowedSids();
		HashSet<string> allowedSids2 = CreateRuntimeAllowedSids(ResolveInteractiveUserSid(runAsUser));
		bool flag = false;
		try
		{
			ValidateExistingProtectedObject(text, allowedSids, "OICI");
			flag = true;
		}
		catch (UnauthorizedAccessException)
		{
			ValidateExistingProtectedObject(text, allowedSids2, "OICI");
			flag = true;
		}
		if (!flag)
		{
			throw new UnauthorizedAccessException("卸载数据隔离根目录不可信。");
		}
		Stack<string> stack = new Stack<string>();
		stack.Push(text);
		while (stack.Count > 0)
		{
			string path2 = stack.Pop();
			RejectReparsePoint(path2);
			foreach (string item in Directory.EnumerateFileSystemEntries(path2))
			{
				RejectReparsePoint(item);
				if (Directory.Exists(item))
				{
					stack.Push(item);
				}
			}
		}
		ProtectRollbackTree(text);
		ValidateProtectedRollbackTree(text);
	}

	public static void ValidateProtectedRollbackTree(string root)
	{
		string text = ValidateRollbackPath(root);
		if (!Directory.Exists(text))
		{
			throw new DirectoryNotFoundException("受保护恢复目录不存在：" + text);
		}
		HashSet<string> allowedSids = CreateRollbackAllowedSids();
		Stack<string> stack = new Stack<string>();
		stack.Push(text);
		while (stack.Count > 0)
		{
			string path = stack.Pop();
			RejectReparsePoint(path);
			ValidateExistingProtectedObject(path, allowedSids, "OICI");
			foreach (string item in Directory.EnumerateFileSystemEntries(path))
			{
				RejectReparsePoint(item);
				bool flag = Directory.Exists(item);
				ValidateExistingProtectedObject(item, allowedSids, flag ? "OICI" : "");
				if (flag)
				{
					stack.Push(item);
				}
			}
		}
		RejectReparsePoint(text);
	}

	public static void NormalizeProtectedRollbackTree(string root)
	{
		string text = ValidateRollbackPath(root);
		ValidateProtectedRollbackDirectory(text);
		Stack<string> stack = new Stack<string>();
		stack.Push(text);
		while (stack.Count > 0)
		{
			string path = stack.Pop();
			RejectReparsePoint(path);
			foreach (string item in Directory.EnumerateFileSystemEntries(path))
			{
				RejectReparsePoint(item);
				if (Directory.Exists(item))
				{
					stack.Push(item);
				}
			}
		}
		ProtectRollbackTree(text);
		ValidateProtectedRollbackTree(text);
	}

	public static void DeleteProtectedRollbackTree(string root)
	{
		string text = ValidateRollbackPath(root);
		if (Directory.Exists(text))
		{
			ValidateProtectedRollbackTree(text);
			Directory.Delete(text, recursive: true);
			if (Directory.Exists(text))
			{
				throw new IOException("受保护恢复目录删除后仍存在：" + text);
			}
		}
	}

	public static void ProtectRollbackTree(string root)
	{
		RejectReparsePoint(root);
		Stack<string> stack = new Stack<string>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			string path = stack.Pop();
			ApplyRollbackDacl(path, isDirectory: true);
			foreach (string item in Directory.EnumerateFileSystemEntries(path))
			{
				RejectReparsePoint(item);
				bool flag = Directory.Exists(item);
				ApplyRollbackDacl(item, flag);
				if (flag)
				{
					stack.Push(item);
				}
			}
		}
		RejectReparsePoint(root);
	}

	private static string ValidateRollbackPath(string path)
	{
		string text = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string text2 = Path.GetFullPath("C:\\Program Files\\P2PAgent").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (!text.StartsWith(text2 + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
		{
			throw new UnauthorizedAccessException("恢复对象必须位于受保护的 InstallRoot 内。");
		}
		return text;
	}

	private static HashSet<string> CreateRollbackAllowedSids()
	{
		return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
			new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value
		};
	}

	public static void RestoreDataRootFromProtectedQuarantine(string runAsUser)
	{
		string fullPath = Path.GetFullPath("C:\\ProgramData\\P2PAgent");
		RejectReparsePoint(fullPath);
		string value = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
		string value2 = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
		HashSet<string> allowedSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { value, value2 };
		if (!TryReadDaclSddl(fullPath, out string daclSddl, out string ownerSid) || !string.Equals(ownerSid, value, StringComparison.OrdinalIgnoreCase) || !IsExactProtectedDacl(daclSddl, allowedSids, "OICI"))
		{
			throw new UnauthorizedAccessException("隔离数据不再具有预期的 BA/SY-only owner/DACL，拒绝恢复。");
		}
		SecurityIdentifier userSid = ResolveAccountSid(runAsUser);
		Stack<string> stack = new Stack<string>();
		stack.Push(fullPath);
		while (stack.Count > 0)
		{
			string path = stack.Pop();
			RejectReparsePoint(path);
			ApplyExactDacl(path, userSid, isDirectory: true);
			foreach (string item in Directory.EnumerateFileSystemEntries(path))
			{
				RejectReparsePoint(item);
				bool flag = Directory.Exists(item);
				ApplyExactDacl(item, userSid, flag);
				if (flag)
				{
					stack.Push(item);
				}
			}
		}
		RejectReparsePoint(fullPath);
	}

	public static string ResolveProtectedDataRootUserSid()
	{
		string fullPath = Path.GetFullPath("C:\\ProgramData\\P2PAgent");
		RejectReparsePoint(fullPath);
		if (!TryReadDaclSddl(fullPath, out string daclSddl, out string ownerSid))
		{
			throw new UnauthorizedAccessException("无法读取 DataRoot ACL 快照。");
		}
		string value = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
		string value2 = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
		MatchCollection matchCollection = Regex.Matches(daclSddl, "\\(A;OICI;FA;;;(?<sid>[^)]*)\\)");
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (Match item in matchCollection)
		{
			string value3 = item.Groups["sid"].Value;
			ownerSid = ((value3 == "BA") ? value : ((!(value3 == "SY")) ? new SecurityIdentifier(value3).Value : value2));
			string text = ownerSid;
			if (!string.Equals(text, value, StringComparison.OrdinalIgnoreCase) && !string.Equals(text, value2, StringComparison.OrdinalIgnoreCase))
			{
				hashSet.Add(text);
			}
		}
		if (!daclSddl.StartsWith("D:P", StringComparison.Ordinal) || matchCollection.Count != 3 || hashSet.Count != 1)
		{
			throw new UnauthorizedAccessException("DataRoot 不是受保护的唯一运行用户/BA/SY 三主体 ACL。");
		}
		return hashSet.Single();
	}

	private static void ApplyRollbackDacl(string path, bool isDirectory)
	{
		string text = (isDirectory ? "OICI" : "");
		if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"O:BAD:P(A;{text};FA;;;BA)(A;{text};FA;;;SY)", 1u, out var securityDescriptor, out var _))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "无法生成隔离对象安全描述符：" + path);
		}
		try
		{
			ApplyNamedSecurityDescriptor(path, securityDescriptor, "无法加固隔离对象 ACL：" + path);
		}
		finally
		{
			LocalFree(securityDescriptor);
		}
		HashSet<string> allowedSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
			new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value
		};
		ValidateExistingProtectedObject(path, allowedSids, text);
		RejectReparsePoint(path);
	}

	private static void ApplyNamedSecurityDescriptor(string path, nint descriptor, string errorMessage)
	{
		if (!GetSecurityDescriptorOwner(descriptor, out var owner, out var ownerDefaulted) || owner == IntPtr.Zero)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), errorMessage + "（无法读取 owner）");
		}
		if (!GetSecurityDescriptorDacl(descriptor, out var daclPresent, out var dacl, out ownerDefaulted) || !daclPresent || dacl == IntPtr.Zero)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), errorMessage + "（无法读取 DACL）");
		}
		uint num = SetNamedSecurityInfo(path, 1, 2147483653u, owner, IntPtr.Zero, dacl, IntPtr.Zero);
		if (num != 0)
		{
			throw new Win32Exception(checked((int)num), errorMessage);
		}
	}

	private static void ValidateAuthenticode(string path, string expectedSignerCommonName, string? expectedLeafCertificateSha256, string? expectedSpkiSha256, IReadOnlyCollection<string>? acceptedSpkiPins = null)
	{
		WinTrustFileInfo winTrustFileInfo = new WinTrustFileInfo(path);
		try
		{
			WinTrustDataNative trustData = new WinTrustDataNative
			{
				StructSize = (uint)Marshal.SizeOf<WinTrustDataNative>(),
				UIChoice = 2u,
				RevocationChecks = 0u,
				UnionChoice = 1u,
				FileInfo = winTrustFileInfo.Pointer,
				StateAction = 1u,
				ProviderFlags = 128u
			};
			try
			{
				Guid actionId = WinTrustActionGenericVerifyV2;
				int num = WinVerifyTrust(IntPtr.Zero, ref actionId, ref trustData);
				if (num != 0)
				{
					throw new InvalidDataException($"p2p-agent.exe Authenticode 验证失败：0x{num:X8}");
				}
				using X509Certificate2 x509Certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
				string nameInfo = x509Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
				string text = Convert.ToHexString(SHA256.HashData(x509Certificate.RawData));
				string text2 = Convert.ToHexString(SHA256.HashData(x509Certificate.PublicKey.ExportSubjectPublicKeyInfo()));
				if (!string.Equals(nameInfo, expectedSignerCommonName, StringComparison.Ordinal) || (expectedLeafCertificateSha256 != null && !string.Equals(text, expectedLeafCertificateSha256, StringComparison.OrdinalIgnoreCase)) || (expectedSpkiSha256 != null && !string.Equals(text2, expectedSpkiSha256, StringComparison.OrdinalIgnoreCase)) || (acceptedSpkiPins != null && !acceptedSpkiPins.Contains<string>(text2, StringComparer.OrdinalIgnoreCase)))
				{
					throw new InvalidDataException($"p2p-agent.exe 签名证书 pin 不匹配。actual CN={nameInfo}, cert={text}, spki={text2}");
				}
			}
			finally
			{
				if (trustData.StateData != IntPtr.Zero)
				{
					trustData.StateAction = 2u;
					Guid actionId2 = WinTrustActionGenericVerifyV2;
					WinVerifyTrust(IntPtr.Zero, ref actionId2, ref trustData);
				}
			}
		}
		finally
		{
			winTrustFileInfo.Dispose();
		}
	}

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool LookupAccountSid(string? systemName, byte[] sid, StringBuilder? name, ref uint nameLength, StringBuilder? referencedDomainName, ref uint referencedDomainNameLength, out SidNameUse use);

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string stringSecurityDescriptor, uint stringSDRevision, out nint securityDescriptor, out uint securityDescriptorSize);

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetNamedSecurityInfoW")]
	private static extern uint SetNamedSecurityInfo(string objectName, int objectType, uint securityInformation, nint owner, nint group, nint dacl, nint sacl);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool GetSecurityDescriptorOwner(nint securityDescriptor, out nint owner, out bool ownerDefaulted);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool GetSecurityDescriptorDacl(nint securityDescriptor, out bool daclPresent, out nint dacl, out bool daclDefaulted);

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(nint securityDescriptor, uint requestedStringSDRevision, uint securityInformation, out nint stringSecurityDescriptor, out uint stringSecurityDescriptorLen);

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool ConvertSidToStringSid(nint sid, out nint stringSid);

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
	private static extern uint GetNamedSecurityInfo(string objectName, int objectType, uint securityInfo, out nint owner, out nint group, out nint dacl, out nint sacl, out nint securityDescriptor);

	[DllImport("advapi32.dll")]
	private static extern uint GetSecurityInfo(nint handle, int objectType, uint securityInfo, out nint owner, out nint group, out nint dacl, out nint sacl, out nint securityDescriptor);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
	private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, FileInfoByHandleClass fileInformationClass, out FileAttributeTagInfo fileInformation, uint bufferSize);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetFileInformationByHandle(SafeFileHandle file, FileInfoByHandleClass fileInformationClass, ref FileDispositionInfo fileInformation, uint bufferSize);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool MoveFileEx(string existingFileName, string? newFileName, uint flags);

	[DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
	private static extern int NetUserGetLocalGroups(string? serverName, string userName, int level, int flags, out nint buffer, int preferredMaximumLength, out int entriesRead, out int totalEntries);

	[DllImport("netapi32.dll")]
	private static extern int NetApiBufferFree(nint buffer);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
	private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder filePath, uint filePathLength, uint flags);

	[DllImport("kernel32.dll")]
	private static extern nint LocalFree(nint memory);

	[DllImport("wintrust.dll", ExactSpelling = true)]
	private static extern int WinVerifyTrust(nint hwnd, ref Guid actionId, ref WinTrustDataNative trustData);
}
