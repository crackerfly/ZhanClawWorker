#!/usr/bin/env python3
"""Cross-platform, non-secret source and payload consistency checks.

This complements the Windows-only Authenticode/Task Scheduler/ACL tests. It never
prints the swarm key body.
"""

from __future__ import annotations

import hashlib
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src" / "ZhanClawControl"
STRINGS = PROJECT / "Localization" / "Strings.cs"
EXPECTED_AGENT_SHA256 = "a2b36af5f2623ddd2f91d223f471abe9d8d957fb2dca6a566e02b2dbd04dd5e9"

failures: list[str] = []


def check(condition: bool, message: str) -> None:
    if not condition:
        failures.append(message)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def parse_catalog(block: str) -> dict[str, str]:
    entry = re.compile(r'\["(?P<key>[A-Za-z0-9_]+)"\]\s*=\s*"(?P<value>(?:\\.|[^"\\])*)"')
    result: dict[str, str] = {}
    for match in entry.finditer(block):
        key = match.group("key")
        # The catalog uses JSON-compatible C# string escapes.
        value = json.loads('"' + match.group("value") + '"')
        if key in result:
            failures.append(f"duplicate localization key: {key}")
        result[key] = value
    return result


def placeholders(value: str) -> set[int]:
    return {int(item) for item in re.findall(r"\{(\d+)(?:[^}]*)\}", value)}


def verify_payload() -> None:
    agent = ROOT / "runtime" / "p2p-agent.exe"
    key = ROOT / "runtime" / "swarm.key"
    manifest_path = ROOT / "runtime" / "payload-manifest.json"
    check(agent.is_file(), "runtime/p2p-agent.exe is missing")
    check(key.is_file(), "runtime/swarm.key is missing")
    check(manifest_path.is_file(), "runtime/payload-manifest.json is missing")
    check(not (ROOT / "runtime" / "swarm(1).key").exists(), "runtime/swarm(1).key must be renamed to swarm.key")
    if not (agent.is_file() and key.is_file() and manifest_path.is_file()):
        return

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    actual_hash = sha256(agent)
    check(actual_hash == EXPECTED_AGENT_SHA256, "p2p-agent.exe does not match the reviewed attachment hash")
    check(manifest.get("sha256", "").lower() == actual_hash, "payload manifest SHA-256 mismatch")
    check(manifest.get("file_name") == "p2p-agent.exe", "payload manifest file_name mismatch")
    check(manifest.get("version") == "0.1.0-integration.4", "payload manifest version mismatch")
    check(manifest.get("pe_machine") == "amd64", "payload manifest PE machine mismatch")
    check(manifest.get("pe_subsystem") == "console", "payload manifest PE subsystem mismatch")
    check(manifest.get("require_authenticode_valid") is True, "payload manifest must require Authenticode")
    for field in (
        "expected_signer_common_name",
        "expected_leaf_certificate_sha256",
        "expected_spki_sha256",
    ):
        check(bool(manifest.get(field)), f"payload manifest missing {field}")
    for field in ("expected_leaf_certificate_sha256", "expected_spki_sha256"):
        check(bool(re.fullmatch(r"[0-9A-Fa-f]{64}", str(manifest.get(field, "")))), f"invalid {field}")
    rollback_pins = manifest.get("trusted_rollback_spki_sha256", [])
    check(bool(rollback_pins), "trusted_rollback_spki_sha256 must not be empty")
    check(all(re.fullmatch(r"[0-9A-Fa-f]{64}", str(pin)) for pin in rollback_pins),
          "trusted_rollback_spki_sha256 contains an invalid pin")
    check(str(manifest.get("expected_spki_sha256", "")).lower() in
          {str(pin).lower() for pin in rollback_pins},
          "trusted_rollback_spki_sha256 must contain expected_spki_sha256")

    try:
        lines = key.read_text(encoding="ascii").splitlines()
    except (UnicodeDecodeError, OSError):
        failures.append("swarm.key is not readable ASCII")
        return
    valid_key = (
        len(lines) == 3
        and lines[0] == "/key/swarm/psk/1.0.0/"
        and lines[1] == "/base16/"
        and re.fullmatch(r"[0-9A-Fa-f]{64}", lines[2]) is not None
    )
    check(valid_key, "swarm.key is not a standard three-line libp2p pnet key")


def verify_xml() -> None:
    paths = sorted(PROJECT.rglob("*.xaml")) + [PROJECT / "ZhanClawControl.csproj", PROJECT / "app.manifest"]
    for path in paths:
        try:
            ET.parse(path)
        except ET.ParseError as error:
            failures.append(f"invalid XML: {path.relative_to(ROOT)}: {error}")


def verify_localization() -> None:
    source = STRINGS.read_text(encoding="utf-8")
    zh_start = source.index("private static readonly IReadOnlyDictionary<string, string> ZhCn")
    zh_end = source.index("private static readonly IReadOnlyDictionary<string, string> ZhTw")
    en_start = source.index("private static IReadOnlyDictionary<string, string> BuildEnglish()")
    en_end = source.index("private static string ToTraditional", en_start)
    zh = parse_catalog(source[zh_start:zh_end])
    en = parse_catalog(source[en_start:en_end])
    check(bool(zh), "Simplified Chinese catalog is empty")
    check(set(en) == set(zh), "English catalog keys do not exactly match Simplified Chinese")
    check("BuildTraditional()" in source and "ToTraditional(pair.Value)" in source,
          "Traditional Chinese must derive every source key without falling back to Simplified Chinese")

    for key in sorted(set(zh) & set(en)):
        check(placeholders(zh[key]) == placeholders(en[key]), f"format placeholder mismatch: {key}")
        check(not re.search(r"[\u3400-\u9fff]", en[key]), f"English catalog contains CJK text: {key}")

    used: set[str] = set()
    dynamic = re.compile(r"DynamicResource\s+([A-Za-z0-9_]+)")
    code_call = re.compile(r'(?:(?<![A-Za-z])\bL|(?<![A-Za-z])\bF|App\.Localization\.Text|App\.Localization\.Format|Localization\.Text|Localization\.Format)\("([A-Za-z0-9_]+)"')
    for path in sorted(PROJECT.rglob("*.xaml")) + sorted(PROJECT.rglob("*.cs")):
        if path == STRINGS:
            continue
        text = path.read_text(encoding="utf-8")
        if path.suffix == ".xaml":
            used.update(dynamic.findall(text))
        used.update(code_call.findall(text))
    # Theme/style resources are not localization strings.
    missing = sorted(key for key in used if key not in zh and not key.endswith("Brush"))
    check(not missing, "missing localization keys: " + ", ".join(missing))


def verify_visual_contract() -> None:
    light = (PROJECT / "Themes" / "Light.xaml").read_text(encoding="utf-8")
    dark = (PROJECT / "Themes" / "Dark.xaml").read_text(encoding="utf-8")
    controls = (PROJECT / "Themes" / "Controls.xaml").read_text(encoding="utf-8")
    views = "\n".join(path.read_text(encoding="utf-8") for path in sorted((PROJECT / "Views").glob("*.*")))
    check('x:Key="BrandFillBrush" Color="#024AD8"' in light, "light theme is missing exact #024AD8 brand fill")
    check('x:Key="BrandFillBrush" Color="#024AD8"' in dark, "dark theme is missing exact #024AD8 brand fill")
    check("BrandFillBrush" in controls, "primary controls do not use the brand fill token")
    check("LinearGradientBrush" not in controls + light + dark, "gradients are outside the selected visual direction")
    check("DropShadowEffect" not in controls + light + dark, "drop shadows are outside the selected visual direction")
    check("Segoe MDL2 Assets" not in views, "legacy Segoe MDL2 icon found")
    check("Glyph=" not in views and "FontIcon" not in views, "font glyph icon found")
    check("PhosphorIcon" in views, "Phosphor icons are not wired into the UI")
    main = (PROJECT / "Views" / "MainWindow.xaml").read_text(encoding="utf-8")
    check('Secondary="{Binding Secondary}"' in main, "navigation is not using Phosphor duotone secondary layers")
    icon_source = (PROJECT / "Views" / "PhosphorIcon.cs").read_text(encoding="utf-8")
    check("Phosphor Core 2.1.1" in icon_source, "Phosphor source/version attribution is missing")
    check((ROOT / "THIRD-PARTY-NOTICES.md").is_file(), "Phosphor third-party notice is missing")


def verify_source_safety_contract() -> None:
    installer = (PROJECT / "Services" / "InstallerService.cs").read_text(encoding="utf-8")
    task = (PROJECT / "Services" / "ScheduledTaskService.cs").read_text(encoding="utf-8")
    host = (PROJECT / "Services" / "AgentHost.cs").read_text(encoding="utf-8")
    process = (PROJECT / "Services" / "ProcessRunner.cs").read_text(encoding="utf-8")
    check("Schedule.Service" in task, "Task Scheduler must use COM rather than localized schtasks output")
    check("RegisterTask(" in task and "StartAsync" in task and "InspectAsync" in task,
          "scheduled-task registration/start inspection boundary is incomplete")
    check("ProcessTerminationUnconfirmedException" in process,
          "unconfirmed child-process termination is not represented")
    check("ValidateRuntimeBoundary(config)" in host and "ValidateAgentPayloadAsync" in host,
          "AgentHost is missing its runtime configuration/payload gate")
    for source in PROJECT.rglob("*.cs"):
        text = source.read_text(encoding="utf-8")
        check("RunAsync(AppPaths.AgentExe" not in text and 'new[] { "-version" }' not in text,
              f"forbidden Agent execution probe: {source.relative_to(ROOT)}")
    check("MoveFileEx" in installer and ".ps1" not in installer,
          "self-delete must not elevate a mutable temporary PowerShell script")
    check('Path.Combine(AppPaths.InstallRoot, $".install-rollback-' in installer,
          "rollback backups must live in a privileged install-root directory")
    check("PrepareSecureRollbackDirectory" in installer,
          "rollback directory must receive a BA/SY-only ACL")
    check("BackupEntry" in installer and "Sha256" in installer,
          "rollback backups must be hash-pinned")
    release = (ROOT / ".github" / "workflows" / "release.yml").read_text(encoding="utf-8")
    check("CODE_SIGN_PFX_B64" in release and "Get-AuthenticodeSignature" in release,
          "formal release workflow must fail closed on outer executable signing")


def main() -> int:
    verify_payload()
    verify_xml()
    verify_localization()
    verify_visual_contract()
    verify_source_safety_contract()
    if failures:
        print("source verification failed:")
        for failure in failures:
            print(f"- {failure}")
        return 1
    print("source verification passed: payload/key shape, XML, localization coverage, and visual contract")
    return 0


if __name__ == "__main__":
    sys.exit(main())
