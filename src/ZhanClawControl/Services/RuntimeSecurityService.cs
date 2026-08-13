using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ZhanClawControl.Services;

/// <summary>
/// 安装边界的安全操作：运行账户解析、DataRoot 的完整 DACL 替换，以及内嵌 Agent 的
/// 精确哈希 / PE / Authenticode 校验，并验证清单元数据结构。
/// 版本是与该 SHA-256 绑定的已审查发布元数据；管理器不会为验证而以管理员身份执行 Agent。
/// </summary>
public static class RuntimeSecurityService
{
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private const uint SddlRevision1 = 1;

    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static SecurityIdentifier ResolveAccountSid(string account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            throw new InvalidDataException("运行账户不能为空。");
        }

        var value = account.Trim();
        try
        {
            if (value.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            {
                return new SecurityIdentifier(value);
            }

            return (SecurityIdentifier)new NTAccount(value).Translate(typeof(SecurityIdentifier));
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException)
        {
            throw new InvalidDataException($"无法解析运行账户：{value}", ex);
        }
    }

    public static void EnsureSafeInstallRoot()
    {
        var actual = Path.GetFullPath(AppPaths.InstallRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var expectedRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetEnvironmentVariable("ProgramW6432")
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFullPath(Path.Combine(value!, "P2PAgent"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!expectedRoots.Any(expected => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)))
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

    /// <summary>
    /// 在任何密钥、身份或 Token 写入前调用。拒绝重解析点，并把目录树中所有现有对象的
    /// DACL 替换为精确的 runAsUser + Administrators + SYSTEM，清除预创建目录遗留的显式 ACE。
    /// </summary>
    public static void PrepareSecureDataRoot(string runAsUser)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DataRoot ACL 加固仅支持 Windows。");
        }

        var sid = ResolveAccountSid(runAsUser);
        var root = Path.GetFullPath(AppPaths.DataRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var expectedRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetEnvironmentVariable("ProgramData")
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFullPath(Path.Combine(value!, "P2PAgent"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!expectedRoots.Any(expected => string.Equals(root, expected, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("DataRoot 路径解析结果异常。");
        }

        if (Directory.Exists(root))
        {
            RejectReparsePoint(root);
            RejectUntrustedExistingSecrets(root, sid);
        }
        else
        {
            Directory.CreateDirectory(root);
        }

        // 敏感文件信任检查通过后，立即锁定根目录关闭写入窗口。每个普通子目录也在枚举
        // 其内容前加固；每项操作前后都复查 reparse，避免递归枚举穿过 junction。
        ApplyExactDacl(root, sid, isDirectory: true);
        RejectReparsePoint(root);
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);
        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            RejectReparsePoint(directory);
            ApplyExactDacl(directory, sid, isDirectory: true);
            RejectReparsePoint(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                RejectReparsePoint(entry);
                var isDirectory = Directory.Exists(entry);
                ApplyExactDacl(entry, sid, isDirectory);
                RejectReparsePoint(entry);
                if (isDirectory)
                {
                    pendingDirectories.Push(entry);
                }
            }
        }

        // ACL 处理期间若目录被替换为 junction，最终检查会阻止继续安装。
        RejectReparsePoint(root);
    }

    public static void ValidateExistingDataRootTrust(string runAsUser)
    {
        if (!Directory.Exists(AppPaths.DataRoot)) return;
        var root = Path.GetFullPath(AppPaths.DataRoot);
        RejectReparsePoint(root);
        RejectUntrustedExistingSecrets(root, ResolveAccountSid(runAsUser));
        RejectReparsePoint(root);
    }

    /// <summary>
    /// 首次接管预创建目录时不能在“锁好 ACL 后”直接信任攻击者预置的身份、Token、配置或 key。
    /// 只要存在敏感文件，原根目录 DACL 必须已经是 protected 且只含目标账户/BA/SY 的 allow ACE。
    /// </summary>
    private static void RejectUntrustedExistingSecrets(string root, SecurityIdentifier runAsSid)
    {
        var sensitiveFiles = new[]
        {
            AppPaths.ConfigFile,
            AppPaths.SwarmKeyFile,
            AppPaths.IdentityFile,
            AppPaths.ApiTokenFile,
            AppPaths.JournalFile
        };
        var allowedSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            runAsSid.Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value
        };
        var existing = sensitiveFiles.Where(path => File.Exists(path) || Directory.Exists(path)).ToList();
        if (existing.Count == 0) return;

        // Pure path-based checks cannot close every rename race. They do, however, fail closed for
        // every object observed here, before its content is consumed, and each later use repeats the
        // reparse check. Windows integration tests must still exercise concurrent replacement cases.
        ValidateExistingProtectedObject(root, allowedSids, expectedAceFlags: "OICI");
        foreach (var sensitiveFile in existing)
        {
            RejectReparsePoint(sensitiveFile);
            if (Directory.Exists(sensitiveFile))
                throw new UnauthorizedAccessException($"敏感文件路径被目录占用：{sensitiveFile}");
            ValidateExistingProtectedObject(sensitiveFile, allowedSids, expectedAceFlags: "", allowExactInheritance: true);
            RejectReparsePoint(sensitiveFile);
        }
    }

    private static void ValidateExistingProtectedObject(
        string path,
        IReadOnlySet<string> allowedSids,
        string expectedAceFlags,
        bool allowExactInheritance = false)
    {
        if (!TryReadDaclSddl(path, out var daclSddl, out var ownerSid) ||
            !allowedSids.Contains(ownerSid) ||
            (!IsExactProtectedDacl(daclSddl, allowedSids, expectedAceFlags) &&
             !(allowExactInheritance && IsExactInheritedFileDacl(daclSddl, allowedSids))))
        {
            throw new UnauthorizedAccessException(
                $"敏感运行对象的 owner/DACL 无法证明安全：{path}。请显式清理或安全迁移后重试。");
        }
    }

    private static bool IsExactInheritedFileDacl(string sddl, IReadOnlySet<string> allowedSids)
    {
        // Files created later by the Agent inherit from the already-protected DataRoot. Accept only
        // the exact three inherited full-control ACEs, never an added explicit principal.
        if (!sddl.StartsWith("D:AI", StringComparison.Ordinal)) return false;
        var aces = Regex.Matches(sddl, @"\((?<type>[^;]*);(?<flags>[^;]*);(?<rights>[^;]*);[^;]*;[^;]*;(?<sid>[^)]*)\)");
        if (aces.Count != allowedSids.Count) return false;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match ace in aces)
        {
            if (ace.Groups["type"].Value != "A" || ace.Groups["flags"].Value != "ID" ||
                ace.Groups["rights"].Value != "FA") return false;
            string sid;
            try
            {
                sid = ace.Groups["sid"].Value switch
                {
                    "BA" => new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
                    "SY" => new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                    var raw => new SecurityIdentifier(raw).Value
                };
            }
            catch { return false; }
            if (!allowedSids.Contains(sid) || !present.Add(sid)) return false;
        }
        return allowedSids.All(present.Contains);
    }

    private static bool TryReadDaclSddl(string path, out string daclSddl, out string ownerSid)
    {
        daclSddl = "";
        ownerSid = "";
        IntPtr descriptor = IntPtr.Zero;
        IntPtr owner = IntPtr.Zero;
        IntPtr group = IntPtr.Zero;
        IntPtr dacl = IntPtr.Zero;
        IntPtr sacl = IntPtr.Zero;
        try
        {
            var result = GetNamedSecurityInfo(
                path,
                1, // SE_FILE_OBJECT
                OwnerSecurityInformation | DaclSecurityInformation,
                out owner,
                out group,
                out dacl,
                out sacl,
                out descriptor);
            if (result != 0 || descriptor == IntPtr.Zero) return false;

            if (owner == IntPtr.Zero || !ConvertSidToStringSid(owner, out var ownerPointer)) return false;
            try
            {
                ownerSid = Marshal.PtrToStringUni(ownerPointer) ?? "";
            }
            finally
            {
                LocalFree(ownerPointer);
            }
            if (ownerSid.Length == 0) return false;

            if (!ConvertSecurityDescriptorToStringSecurityDescriptor(
                    descriptor,
                    SddlRevision1,
                    DaclSecurityInformation,
                    out var sddlPointer,
                    out _)) return false;
            try
            {
                daclSddl = Marshal.PtrToStringUni(sddlPointer) ?? "";
                return daclSddl.Length > 0;
            }
            finally
            {
                LocalFree(sddlPointer);
            }
        }
        finally
        {
            if (descriptor != IntPtr.Zero) LocalFree(descriptor);
        }
    }

    private static bool IsExactProtectedDacl(
        string sddl,
        IReadOnlySet<string> allowedSids,
        string expectedAceFlags)
    {
        if (!sddl.StartsWith("D:P", StringComparison.Ordinal)) return false;
        var aces = System.Text.RegularExpressions.Regex.Matches(sddl, @"\((?<type>[^;]*);(?<flags>[^;]*);(?<rights>[^;]*);[^;]*;[^;]*;(?<sid>[^)]*)\)");
        if (aces.Count != allowedSids.Count) return false;

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match ace in aces)
        {
            if (!string.Equals(ace.Groups["type"].Value, "A", StringComparison.Ordinal) ||
                !string.Equals(ace.Groups["rights"].Value, "FA", StringComparison.Ordinal) ||
                !string.Equals(ace.Groups["flags"].Value, expectedAceFlags, StringComparison.Ordinal)) return false;

            var rawSid = ace.Groups["sid"].Value;
            string canonicalSid;
            try
            {
                canonicalSid = rawSid switch
                {
                    "BA" => new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
                    "SY" => new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                    _ => new SecurityIdentifier(rawSid).Value
                };
            }
            catch
            {
                return false;
            }

            if (!allowedSids.Contains(canonicalSid) || !present.Add(canonicalSid)) return false;
        }

        return allowedSids.All(present.Contains);
    }

    /// <summary>
    /// Revalidates the protected parent used by atomic configuration writes. This is a fail-closed
    /// path check, not a claim that path APIs can eliminate concurrent rename races.
    /// </summary>
    public static void ValidateSecureDataRootForWrite()
    {
        var root = Path.GetFullPath(AppPaths.DataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("受保护的数据目录不存在。");
        RejectReparsePoint(root);
        if (!TryReadDaclSddl(root, out var sddl, out var ownerSid))
            throw new UnauthorizedAccessException("无法验证数据目录 owner/DACL。");
        var aceSids = ParseExactFullControlSids(sddl, "OICI");
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
        if (aceSids is null || aceSids.Count != 3 || !aceSids.Contains(administrators) ||
            !aceSids.Contains(system) || !string.Equals(ownerSid, administrators, StringComparison.OrdinalIgnoreCase) ||
            aceSids.Count(sid => !string.Equals(sid, administrators, StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(sid, system, StringComparison.OrdinalIgnoreCase)) != 1)
            throw new UnauthorizedAccessException("数据目录不是受保护的三主体专用 ACL。");
        RejectReparsePoint(root);
    }

    public static void ValidateRuntimeSecretsForCurrentUser()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("运行时 ACL 校验仅支持 Windows。");
        var currentSid = WindowsIdentity.GetCurrent().User
                         ?? throw new UnauthorizedAccessException("无法读取当前运行账户 SID。");
        var allowedSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            currentSid.Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value
        };
        ValidateExistingProtectedObject(AppPaths.DataRoot, allowedSids, "OICI");
        foreach (var path in new[]
                 {
                     AppPaths.ConfigFile, AppPaths.SwarmKeyFile, AppPaths.IdentityFile,
                     AppPaths.ApiTokenFile, AppPaths.JournalFile
                 }.Where(File.Exists))
        {
            RejectReparsePoint(path);
            ValidateExistingProtectedObject(path, allowedSids, "", allowExactInheritance: true);
            RejectReparsePoint(path);
        }
    }

    private static HashSet<string>? ParseExactFullControlSids(string sddl, string expectedFlags)
    {
        if (!sddl.StartsWith("D:P", StringComparison.Ordinal)) return null;
        var matches = Regex.Matches(sddl, @"\((?<type>[^;]*);(?<flags>[^;]*);(?<rights>[^;]*);[^;]*;[^;]*;(?<sid>[^)]*)\)");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match ace in matches)
        {
            if (ace.Groups["type"].Value != "A" || ace.Groups["rights"].Value != "FA" ||
                ace.Groups["flags"].Value != expectedFlags) return null;
            string sid;
            try
            {
                sid = ace.Groups["sid"].Value switch
                {
                    "BA" => new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
                    "SY" => new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                    var raw => new SecurityIdentifier(raw).Value
                };
            }
            catch { return null; }
            if (!result.Add(sid)) return null;
        }
        return result.Count == matches.Count ? result : null;
    }

    public static void ValidateSwarmKey(string path)
    {
        RejectReparsePoint(path);
        var lines = File.ReadAllLines(path);
        if (lines.Length != 3 ||
            lines[0] != "/key/swarm/psk/1.0.0/" ||
            lines[1] != "/base16/" ||
            lines[2].Length != 64 ||
            lines[2].Any(c => !Uri.IsHexDigit(c)))
        {
            throw new InvalidDataException("swarm.key 格式无效；期望标准 libp2p pnet base16 三行格式。");
        }
    }

    public static async Task ValidateAgentPayloadAsync(string path, CancellationToken ct = default)
    {
        RejectReparsePoint(path);
        var manifest = LoadPayloadManifest();
        if (!string.Equals(Path.GetFileName(path), manifest.FileName, StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(path).StartsWith(manifest.FileName + ".", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Agent payload 文件名异常：{Path.GetFileName(path)}");
        }

        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false))
                .ToLowerInvariant();
            if (!string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"p2p-agent.exe SHA-256 不匹配。expected={manifest.Sha256} actual={hash}");
            }
        }

        ValidatePe(path);
        if (manifest.RequireAuthenticodeValid)
        {
            ValidateAuthenticode(path, manifest.ExpectedSignerCommonName,
                manifest.ExpectedLeafCertificateSha256, manifest.ExpectedSpkiSha256);
        }

        // Never execute an uninstalled payload in an elevated process. SHA-256 plus PE shape and
        // Authenticode certificate/SPKI pins uniquely identify the reviewed bytes. Version remains
        // release metadata and is validated structurally when the manifest is loaded.
    }

    /// <summary>
    /// Validates a previously installed Agent for rollback without requiring it to match the
    /// new package's exact version/hash. The rollback layer separately pins the captured bytes
    /// with SHA-256; this gate proves PE shape and the frozen trusted publisher/certificate.
    /// </summary>
    public static void ValidateTrustedAgentPublisher(string path)
    {
        RejectReparsePoint(path);
        var manifest = LoadPayloadManifest();
        ValidatePe(path);
        if (!manifest.RequireAuthenticodeValid)
            throw new InvalidDataException("payload manifest 未要求 Authenticode，不能用于旧版 Agent 回滚信任。");
        ValidateAuthenticode(path, manifest.ExpectedSignerCommonName,
            manifest.ExpectedLeafCertificateSha256, manifest.ExpectedSpkiSha256);
    }

    /// <summary>
    /// Rotation-tolerant rollback gate: requires a valid Windows trust chain, exact publisher common
    /// name and PE shape, but deliberately does not require the new package's leaf/SPKI pins.
    /// </summary>
    public static void ValidateTrustedAgentPublisherForRollback(string path)
    {
        RejectReparsePoint(path);
        var manifest = LoadPayloadManifest();
        ValidatePe(path);
        ValidateAuthenticodePublisher(path, manifest.ExpectedSignerCommonName, manifest.TrustedRollbackSpkiSha256);
    }

    private static void ValidateAuthenticodePublisher(
        string path,
        string expectedSignerCommonName,
        IReadOnlyCollection<string> trustedSpkiPins)
    {
        ValidateAuthenticode(path, expectedSignerCommonName, null, null, trustedSpkiPins);
    }

    private static void ValidatePe(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != 0x5A4D)
        {
            throw new InvalidDataException("p2p-agent.exe 缺少 MZ 头。");
        }

        stream.Position = 0x3c;
        var peOffset = reader.ReadInt32();
        if (peOffset < 0x40 || peOffset > stream.Length - 26)
        {
            throw new InvalidDataException("p2p-agent.exe PE 头偏移无效。");
        }

        stream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550)
        {
            throw new InvalidDataException("p2p-agent.exe 缺少 PE 签名。");
        }

        if (reader.ReadUInt16() != 0x8664)
        {
            throw new InvalidDataException("p2p-agent.exe 不是 AMD64 PE。");
        }

        stream.Position = peOffset + 24;
        var optionalMagic = reader.ReadUInt16();
        if (optionalMagic != 0x20b)
        {
            throw new InvalidDataException("p2p-agent.exe 不是 PE32+。");
        }

        stream.Position = peOffset + 24 + 68;
        var subsystem = reader.ReadUInt16();
        if (subsystem != 3)
        {
            throw new InvalidDataException("p2p-agent.exe 不是 Console 子系统程序。");
        }
    }

    private static PayloadManifest LoadPayloadManifest()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(AppPaths.PayloadManifestResource)
            ?? throw new FileNotFoundException("安装包缺少 payload-manifest.json。");
        var manifest = JsonSerializer.Deserialize<PayloadManifest>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException("payload-manifest.json 无法解析。");

        if (manifest.SchemaVersion != 1 ||
            manifest.FileName != "p2p-agent.exe" ||
            manifest.Sha256.Length != 64 ||
            manifest.Sha256.Any(c => !Uri.IsHexDigit(c)) ||
            string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.ExpectedSignerCommonName) ||
            manifest.ExpectedLeafCertificateSha256.Length != 64 ||
            manifest.ExpectedSpkiSha256.Length != 64)
        {
            throw new InvalidDataException("payload-manifest.json 内容无效。");
        }
        if (manifest.TrustedRollbackSpkiSha256.Count == 0 ||
            manifest.TrustedRollbackSpkiSha256.Any(pin => pin.Length != 64 || pin.Any(c => !Uri.IsHexDigit(c))))
            throw new InvalidDataException("payload-manifest.json 缺少有效 trusted rollback SPKI pins。");

        return manifest;
    }

    public static void RejectReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"拒绝访问重解析点：{path}");
        }
    }

    private static void ApplyExactDacl(string path, SecurityIdentifier userSid, bool isDirectory)
    {
        var inheritance = isDirectory ? "OICI" : "";
        var sddl = $"O:BAD:P(A;{inheritance};FA;;;{userSid.Value})(A;{inheritance};FA;;;BA)(A;{inheritance};FA;;;SY)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl,
                SddlRevision1,
                out var descriptor,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法生成安全描述符：{path}");
        }

        try
        {
            if (!SetFileSecurity(
                    path,
                    OwnerSecurityInformation | DaclSecurityInformation | ProtectedDaclSecurityInformation,
                    descriptor))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法设置完整 DACL：{path}");
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static void ApplyInstallRootDacl(string path)
    {
        // 程序目录不授予运行账户写权限；BA/SY 可维护，BU 只读执行。
        const string sddl = "O:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;0x1200a9;;;BU)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl,
                SddlRevision1,
                out var descriptor,
                out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法生成程序目录安全描述符：{path}");
        try
        {
            if (!SetFileSecurity(
                    path,
                    OwnerSecurityInformation | DaclSecurityInformation | ProtectedDaclSecurityInformation,
                    descriptor))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法加固程序目录 ACL：{path}");
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    public static void PrepareSecureRollbackDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(AppPaths.InstallRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("回滚目录必须位于受保护的 InstallRoot 内。");
        Directory.CreateDirectory(fullPath);
        RejectReparsePoint(fullPath);
        // Backups contain config and swarm credentials. Do not grant Builtin Users or runAs access.
        const string sddl = "O:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, SddlRevision1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法生成回滚目录安全描述符。");
        try
        {
            if (!SetFileSecurity(fullPath,
                    OwnerSecurityInformation | DaclSecurityInformation | ProtectedDaclSecurityInformation,
                    descriptor))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加固回滚目录 ACL。");
        }
        finally { LocalFree(descriptor); }
        RejectReparsePoint(fullPath);
    }

    public static void ProtectRollbackTree(string root)
    {
        RejectReparsePoint(root);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            ApplyRollbackDacl(directory, true);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectReparsePoint(entry);
                var isDirectory = Directory.Exists(entry);
                ApplyRollbackDacl(entry, isDirectory);
                if (isDirectory) pending.Push(entry);
            }
        }
        RejectReparsePoint(root);
    }

    public static void RestoreDataRootFromProtectedQuarantine(string runAsUser)
    {
        var root = Path.GetFullPath(AppPaths.DataRoot);
        RejectReparsePoint(root);
        var ba = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        var sy = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
        var quarantineSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ba, sy };
        if (!TryReadDaclSddl(root, out var sddl, out var owner) ||
            !string.Equals(owner, ba, StringComparison.OrdinalIgnoreCase) ||
            !IsExactProtectedDacl(sddl, quarantineSids, "OICI"))
            throw new UnauthorizedAccessException("隔离数据不再具有预期的 BA/SY-only owner/DACL，拒绝恢复。");

        var runAsSid = ResolveAccountSid(runAsUser);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            RejectReparsePoint(directory);
            ApplyExactDacl(directory, runAsSid, true);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectReparsePoint(entry);
                var isDirectory = Directory.Exists(entry);
                ApplyExactDacl(entry, runAsSid, isDirectory);
                if (isDirectory) pending.Push(entry);
            }
        }
        RejectReparsePoint(root);
    }

    public static string ResolveProtectedDataRootUserSid()
    {
        var root = Path.GetFullPath(AppPaths.DataRoot);
        RejectReparsePoint(root);
        if (!TryReadDaclSddl(root, out var sddl, out _))
            throw new UnauthorizedAccessException("无法读取 DataRoot ACL 快照。");
        var ba = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        var sy = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
        var matches = Regex.Matches(sddl, @"\(A;OICI;FA;;;(?<sid>[^)]*)\)");
        var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in matches)
        {
            var raw = match.Groups["sid"].Value;
            var sid = raw switch { "BA" => ba, "SY" => sy, _ => new SecurityIdentifier(raw).Value };
            if (!string.Equals(sid, ba, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(sid, sy, StringComparison.OrdinalIgnoreCase)) users.Add(sid);
        }
        if (!sddl.StartsWith("D:P", StringComparison.Ordinal) || matches.Count != 3 || users.Count != 1)
            throw new UnauthorizedAccessException("DataRoot 不是受保护的唯一运行用户/BA/SY 三主体 ACL。");
        return users.Single();
    }

    private static void ApplyRollbackDacl(string path, bool isDirectory)
    {
        var flags = isDirectory ? "OICI" : "";
        var sddl = $"O:BAD:P(A;{flags};FA;;;BA)(A;{flags};FA;;;SY)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, SddlRevision1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法生成隔离对象安全描述符：" + path);
        try
        {
            if (!SetFileSecurity(path, OwnerSecurityInformation | DaclSecurityInformation | ProtectedDaclSecurityInformation, descriptor))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加固隔离对象 ACL：" + path);
        }
        finally { LocalFree(descriptor); }
    }

    private static void ValidateAuthenticode(
        string path,
        string expectedSignerCommonName,
        string? expectedLeafCertificateSha256,
        string? expectedSpkiSha256,
        IReadOnlyCollection<string>? acceptedSpkiPins = null)
    {
        var fileInfo = new WinTrustFileInfo(path);
        try
        {
            var data = new WinTrustDataNative
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustDataNative>(),
                UIChoice = 2, // WTD_UI_NONE
                RevocationChecks = 0,
                UnionChoice = 1, // WTD_CHOICE_FILE
                FileInfo = fileInfo.Pointer,
                StateAction = 1, // WTD_STATEACTION_VERIFY
                ProviderFlags = 0x00000080 // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            try
            {
                var action = WinTrustActionGenericVerifyV2;
                var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                if (result != 0)
                {
                    throw new InvalidDataException($"p2p-agent.exe Authenticode 验证失败：0x{result:X8}");
                }

                using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                var commonName = signer.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                var certificateHash = Convert.ToHexString(SHA256.HashData(signer.RawData));
                var spkiHash = Convert.ToHexString(SHA256.HashData(signer.PublicKey.ExportSubjectPublicKeyInfo()));
                if (!string.Equals(commonName, expectedSignerCommonName, StringComparison.Ordinal) ||
                    (expectedLeafCertificateSha256 is not null &&
                     !string.Equals(certificateHash, expectedLeafCertificateSha256, StringComparison.OrdinalIgnoreCase)) ||
                    (expectedSpkiSha256 is not null &&
                     !string.Equals(spkiHash, expectedSpkiSha256, StringComparison.OrdinalIgnoreCase)) ||
                    (acceptedSpkiPins is not null &&
                     !acceptedSpkiPins.Contains(spkiHash, StringComparer.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException(
                        $"p2p-agent.exe 签名证书 pin 不匹配。actual CN={commonName}, cert={certificateHash}, spki={spkiHash}");
                }
            }
            finally
            {
                if (data.StateData != IntPtr.Zero)
                {
                    data.StateAction = 2; // WTD_STATEACTION_CLOSE
                    var action = WinTrustActionGenericVerifyV2;
                    WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                }
            }
        }
        finally
        {
            fileInfo.Dispose();
        }
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

        [JsonPropertyName("require_authenticode_valid")]
        public bool RequireAuthenticodeValid { get; set; }

        [JsonPropertyName("expected_signer_common_name")]
        public string ExpectedSignerCommonName { get; set; } = "";

        [JsonPropertyName("expected_leaf_certificate_sha256")]
        public string ExpectedLeafCertificateSha256 { get; set; } = "";

        [JsonPropertyName("expected_spki_sha256")]
        public string ExpectedSpkiSha256 { get; set; } = "";

        [JsonPropertyName("trusted_rollback_spki_sha256")]
        public List<string> TrustedRollbackSpkiSha256 { get; set; } = new();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfoNative
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    private sealed class WinTrustFileInfo : IDisposable
    {
        public IntPtr Pointer { get; }
        private readonly IntPtr _path;

        public WinTrustFileInfo(string path)
        {
            _path = Marshal.StringToCoTaskMemUni(path);
            var native = new WinTrustFileInfoNative
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfoNative>(),
                FilePath = _path
            };
            Pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfoNative>());
            Marshal.StructureToPtr(native, Pointer, false);
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
        public IntPtr PolicyCallbackData;
        public IntPtr SIPClientData;
        public uint UIChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public string? URLReference;
        public uint ProviderFlags;
        public uint UIContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSDRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetFileSecurity(
        string fileName,
        uint securityInformation,
        IntPtr securityDescriptor);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(
        IntPtr securityDescriptor,
        uint requestedStringSDRevision,
        uint securityInformation,
        out IntPtr stringSecurityDescriptor,
        out uint stringSecurityDescriptorLen);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertSidToStringSid(
        IntPtr sid,
        out IntPtr stringSid);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetNamedSecurityInfo(
        string objectName,
        int objectType,
        uint securityInfo,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        ref Guid actionId,
        ref WinTrustDataNative trustData);
}
