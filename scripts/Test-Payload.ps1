[CmdletBinding()]
param(
    [string]$ManifestPath = 'runtime/payload-manifest.json',
    [string]$AgentPath = 'runtime/p2p-agent.exe',
    [string]$SwarmKeyPath = 'runtime/swarm.key',
    [switch]$RequireSwarmKey
)

$ErrorActionPreference = 'Stop'

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

$stream = [IO.File]::Open($agentFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $reader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        if (-not $reader.HasMetadata -and $null -eq $reader.PEHeaders.PEHeader) {
            throw 'p2p-agent.exe 不是有效 PE 文件。'
        }

        if ([string]$reader.PEHeaders.CoffHeader.Machine -ne 'Amd64') {
            throw "PE 架构不是 AMD64：$($reader.PEHeaders.CoffHeader.Machine)"
        }

        if ([string]$reader.PEHeaders.PEHeader.Subsystem -ne 'WindowsCui') {
            throw "PE 子系统不是 Console：$($reader.PEHeaders.PEHeader.Subsystem)"
        }
    }
    finally {
        $reader.Dispose()
    }
}
finally {
    $stream.Dispose()
}

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
    $actualLeafHash = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($signature.SignerCertificate.RawData)
    ).ToLowerInvariant()
    if ($expectedLeafHash -notmatch '^[0-9a-f]{64}$' -or $actualLeafHash -cne $expectedLeafHash) {
        throw "p2p-agent.exe 签名证书 SHA-256 不匹配。expected=$expectedLeafHash actual=$actualLeafHash"
    }

    $expectedSpkiHash = ([string]$manifest.expected_spki_sha256).Trim().ToLowerInvariant()
    $actualSpkiHash = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData(
            $signature.SignerCertificate.PublicKey.ExportSubjectPublicKeyInfo()
        )
    ).ToLowerInvariant()
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
