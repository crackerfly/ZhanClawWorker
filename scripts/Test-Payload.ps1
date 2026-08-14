[CmdletBinding()]
param(
    [string]$ManifestPath = 'runtime/payload-manifest.json',
    [string]$AgentPath = 'runtime/p2p-agent.exe',
    [string]$SwarmKeyPath = 'runtime/swarm.key',
    [switch]$RequireSwarmKey
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 5 -or
    ($PSVersionTable.PSVersion.Major -eq 5 -and $PSVersionTable.PSVersion.Minor -lt 1)) {
    throw 'Test-Payload.ps1 需要 Windows PowerShell 5.1 或 PowerShell 7+。'
}

function Get-Sha256HexFromBytes([byte[]]$Bytes) {
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash($Bytes)
        return ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Join-ByteArrays([byte[]]$First, [byte[]]$Second) {
    [byte[]]$result = [Array]::CreateInstance([byte], $First.Length + $Second.Length)
    [Buffer]::BlockCopy($First, 0, $result, 0, $First.Length)
    [Buffer]::BlockCopy($Second, 0, $result, $First.Length, $Second.Length)
    return $result
}

function ConvertTo-DerLength([int]$Length) {
    if ($Length -lt 0) {
        throw 'DER 长度不能为负数。'
    }

    if ($Length -lt 128) {
        return [byte[]]@([byte]$Length)
    }

    $octets = New-Object 'System.Collections.Generic.List[byte]'
    $remaining = [uint32]$Length
    while ($remaining -gt 0) {
        [void]$octets.Add([byte]($remaining -band 0xff))
        $remaining = $remaining -shr 8
    }

    [byte[]]$encoded = [Array]::CreateInstance([byte], 1 + $octets.Count)
    $encoded[0] = [byte](0x80 -bor $octets.Count)
    for ($index = 0; $index -lt $octets.Count; $index++) {
        $encoded[$index + 1] = $octets[$octets.Count - 1 - $index]
    }
    return $encoded
}

function New-DerElement([byte]$Tag, [byte[]]$Content) {
    [byte[]]$length = ConvertTo-DerLength $Content.Length
    [byte[]]$result = [Array]::CreateInstance([byte], 1 + $length.Length + $Content.Length)
    $result[0] = $Tag
    [Buffer]::BlockCopy($length, 0, $result, 1, $length.Length)
    [Buffer]::BlockCopy($Content, 0, $result, 1 + $length.Length, $Content.Length)
    return $result
}

function Add-OidArc(
    [System.Collections.Generic.List[byte]]$Target,
    [uint64]$Value
) {
    $encoded = New-Object 'System.Collections.Generic.List[byte]'
    do {
        [void]$encoded.Add([byte]($Value -band [uint64]0x7f))
        $Value = $Value -shr 7
    } while ($Value -gt 0)

    for ($index = $encoded.Count - 1; $index -ge 0; $index--) {
        $octet = $encoded[$index]
        if ($index -ne 0) {
            $octet = [byte]($octet -bor 0x80)
        }
        [void]$Target.Add($octet)
    }
}

function ConvertTo-DerOidContent([string]$Oid) {
    if ($Oid -notmatch '^\d+(?:\.\d+)+$') {
        throw "证书公钥算法 OID 无效：$Oid"
    }

    [uint64[]]$arcs = @($Oid.Split('.') | ForEach-Object { [uint64]::Parse($_) })
    if ($arcs.Count -lt 2 -or $arcs[0] -gt 2 -or ($arcs[0] -lt 2 -and $arcs[1] -gt 39)) {
        throw "证书公钥算法 OID 无效：$Oid"
    }

    $result = New-Object 'System.Collections.Generic.List[byte]'
    Add-OidArc $result ([uint64](40 * $arcs[0] + $arcs[1]))
    for ($index = 2; $index -lt $arcs.Count; $index++) {
        Add-OidArc $result $arcs[$index]
    }
    return $result.ToArray()
}

function Get-SubjectPublicKeyInfo([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    # X509Certificate2.PublicKey exposes the exact ASN.1 algorithm parameters and
    # encoded key value on .NET Framework 4.8. Re-wrap those values as RFC 5280
    # SubjectPublicKeyInfo so this check works in Windows PowerShell 5.1 without
    # relying on the newer public-key export helper from modern .NET runtimes.
    $oidValue = [string]$Certificate.PublicKey.Oid.Value
    [byte[]]$oidContent = ConvertTo-DerOidContent $oidValue
    [byte[]]$oidElement = New-DerElement 0x06 $oidContent
    [byte[]]$parameters = @($Certificate.PublicKey.EncodedParameters.RawData)
    [byte[]]$algorithmContent = Join-ByteArrays $oidElement $parameters
    [byte[]]$algorithm = New-DerElement 0x30 $algorithmContent

    [byte[]]$keyValue = @($Certificate.PublicKey.EncodedKeyValue.RawData)
    [byte[]]$bitStringContent = [Array]::CreateInstance([byte], 1 + $keyValue.Length)
    # First byte is the number of unused bits. Public keys used here are byte-aligned.
    $bitStringContent[0] = 0
    [Buffer]::BlockCopy($keyValue, 0, $bitStringContent, 1, $keyValue.Length)
    [byte[]]$bitString = New-DerElement 0x03 $bitStringContent

    [byte[]]$spkiContent = Join-ByteArrays $algorithm $bitString
    return (New-DerElement 0x30 $spkiContent)
}

function Test-PeLayout([string]$Path, [string]$ExpectedMachine, [string]$ExpectedSubsystem) {
    $machineLabel = $ExpectedMachine.Trim().ToLowerInvariant()
    $subsystemLabel = $ExpectedSubsystem.Trim().ToLowerInvariant()
    if ($machineLabel -ne 'amd64') {
        throw "payload manifest 的 pe_machine 不受支持：$ExpectedMachine"
    }
    if ($subsystemLabel -ne 'console') {
        throw "payload manifest 的 pe_subsystem 不受支持：$ExpectedSubsystem"
    }

    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($stream.Length -lt 96) {
            throw 'p2p-agent.exe 不是有效 PE 文件：文件过短。'
        }

        $stream.Position = 0
        if ($reader.ReadUInt16() -ne 0x5a4d) {
            throw 'p2p-agent.exe 不是有效 PE 文件：缺少 MZ 头。'
        }

        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or ([long]$peOffset + 94) -gt $stream.Length) {
            throw 'p2p-agent.exe 不是有效 PE 文件：PE 头越界。'
        }

        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw 'p2p-agent.exe 不是有效 PE 文件：缺少 PE 签名。'
        }

        $machine = $reader.ReadUInt16()
        if ($machine -ne 0x8664) {
            throw ('PE 架构不是 AMD64：0x{0:x4}' -f $machine)
        }

        $stream.Position = $peOffset + 20
        $optionalHeaderSize = $reader.ReadUInt16()
        if ($optionalHeaderSize -lt 70 -or
            ([long]$peOffset + 24 + $optionalHeaderSize) -gt $stream.Length) {
            throw 'p2p-agent.exe 不是有效 PE 文件：可选头越界。'
        }

        $stream.Position = $peOffset + 24
        if ($reader.ReadUInt16() -ne 0x020b) {
            throw 'p2p-agent.exe 不是 PE32+ 文件。'
        }

        $stream.Position = $peOffset + 92
        $subsystem = $reader.ReadUInt16()
        if ($subsystem -ne 3) {
            throw "PE 子系统不是 Console：$subsystem"
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Resolve-ExistingFile([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label 不存在：$Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

$manifestFile = Resolve-ExistingFile $ManifestPath 'payload manifest'
$agentFile = Resolve-ExistingFile $AgentPath 'p2p-agent.exe'
$manifest = Get-Content -LiteralPath $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json

if ($manifest.schema_version -ne 1) {
    throw "不支持的 payload manifest schema：$($manifest.schema_version)"
}

if ($manifest.file_name -ne 'p2p-agent.exe') {
    throw "payload 文件名不匹配：$($manifest.file_name)"
}

$actualHash = (Get-FileHash -LiteralPath $agentFile -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedHash = ([string]$manifest.sha256).Trim().ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    throw "p2p-agent.exe SHA-256 不匹配。expected=$expectedHash actual=$actualHash"
}

Test-PeLayout $agentFile ([string]$manifest.pe_machine) ([string]$manifest.pe_subsystem)

if ([bool]$manifest.require_authenticode_valid) {
    $signature = Get-AuthenticodeSignature -LiteralPath $agentFile
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "p2p-agent.exe Authenticode 无效：$($signature.Status) $($signature.StatusMessage)"
    }

    if ($null -eq $signature.SignerCertificate) {
        throw 'p2p-agent.exe 签名缺少签名者证书。'
    }

    $expectedSigner = ([string]$manifest.expected_signer_common_name).Trim()
    $actualSigner = $signature.SignerCertificate.GetNameInfo(
        [Security.Cryptography.X509Certificates.X509NameType]::SimpleName,
        $false)
    if ([string]::IsNullOrWhiteSpace($expectedSigner) -or $actualSigner -cne $expectedSigner) {
        throw "p2p-agent.exe 签名者 Common Name 不匹配。expected=$expectedSigner actual=$actualSigner"
    }

    $expectedLeafHash = ([string]$manifest.expected_leaf_certificate_sha256).Trim().ToLowerInvariant()
    $actualLeafHash = Get-Sha256HexFromBytes $signature.SignerCertificate.RawData
    if ($expectedLeafHash -notmatch '^[0-9a-f]{64}$' -or $actualLeafHash -cne $expectedLeafHash) {
        throw "p2p-agent.exe 签名证书 SHA-256 不匹配。expected=$expectedLeafHash actual=$actualLeafHash"
    }

    $expectedSpkiHash = ([string]$manifest.expected_spki_sha256).Trim().ToLowerInvariant()
    [byte[]]$subjectPublicKeyInfo = Get-SubjectPublicKeyInfo $signature.SignerCertificate
    $actualSpkiHash = Get-Sha256HexFromBytes $subjectPublicKeyInfo
    if ($expectedSpkiHash -notmatch '^[0-9a-f]{64}$' -or $actualSpkiHash -cne $expectedSpkiHash) {
        throw "p2p-agent.exe 签名公钥 SPKI SHA-256 不匹配。expected=$expectedSpkiHash actual=$actualSpkiHash"
    }
}

$expectedVersion = ([string]$manifest.version).Trim()
if ([string]::IsNullOrWhiteSpace($expectedVersion)) {
    throw 'payload manifest version 不能为空。'
}
$rollbackPins = @($manifest.trusted_rollback_spki_sha256)
if ($rollbackPins.Count -eq 0 -or
    @($rollbackPins | Where-Object { [string]$_ -notmatch '^[0-9A-Fa-f]{64}$' }).Count -ne 0 -or
    -not ($rollbackPins -icontains ([string]$manifest.expected_spki_sha256))) {
    throw 'trusted_rollback_spki_sha256 必须非空、每项为 64 位十六进制，并包含当前 expected_spki_sha256。'
}

if ($RequireSwarmKey) {
    $keyFile = Resolve-ExistingFile $SwarmKeyPath 'swarm.key'
    $lines = @(Get-Content -LiteralPath $keyFile -Encoding ASCII)
    if ($lines.Count -ne 3 -or
        $lines[0] -ne '/key/swarm/psk/1.0.0/' -or
        $lines[1] -ne '/base16/' -or
        $lines[2] -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'swarm.key 格式无效；期望标准 libp2p pnet base16 三行格式。'
    }
}

Write-Host "payload 静态验证通过：SHA-256、AMD64/Console PE、Authenticode pins 与 manifest 元数据均匹配（未执行 Agent）。"
if ($RequireSwarmKey) {
    Write-Host 'swarm.key 验证通过（未输出密钥内容）。'
}
