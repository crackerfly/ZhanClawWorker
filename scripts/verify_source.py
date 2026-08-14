#!/usr/bin/env python3
"""Cross-platform, non-secret source and payload consistency checks.

This complements the Windows-only Authenticode/Task Scheduler/ACL tests. It never
prints the swarm key body.
"""

from __future__ import annotations

import hashlib
import json
import re
import struct
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src" / "ZhanClawControl"
STRINGS = PROJECT / "Localization" / "Strings.cs"
EXPECTED_AGENT_SHA256 = "a2b36af5f2623ddd2f91d223f471abe9d8d957fb2dca6a566e02b2dbd04dd5e9"
EXPECTED_APP_ICON_SOURCE_SHA256 = "2cb51252846e77f3c0190a48724c036e4247b104da37fa713feb11619fcb1d1e"

failures: list[str] = []


def check(condition: bool, message: str) -> None:
    if not condition:
        failures.append(message)


def compact(source: str) -> str:
    """Normalize layout without changing the tokens a contract check relies on."""
    return re.sub(r"\s+", " ", source).strip()


def balanced_calls(source: str, prefixes: tuple[str, ...]) -> list[str]:
    """Extract complete C# invocations while ignoring delimiters in literals/comments."""
    calls: list[str] = []
    cursor = 0
    while cursor < len(source):
        matches = [(source.find(prefix, cursor), prefix) for prefix in prefixes]
        matches = [(index, prefix) for index, prefix in matches if index >= 0]
        if not matches:
            break
        start, prefix = min(matches)
        opening = start + len(prefix)
        while opening < len(source) and source[opening].isspace():
            opening += 1
        if opening >= len(source) or source[opening] != "(":
            cursor = start + len(prefix)
            continue
        depth = 0
        index = opening
        state = "code"
        while index < len(source):
            char = source[index]
            following = source[index + 1] if index + 1 < len(source) else ""
            if state == "code":
                if char == '"':
                    state = "string"
                elif char == "'":
                    state = "char"
                elif char == "/" and following == "/":
                    state = "line-comment"
                    index += 1
                elif char == "/" and following == "*":
                    state = "block-comment"
                    index += 1
                elif char == "(":
                    depth += 1
                elif char == ")":
                    depth -= 1
                    if depth == 0:
                        calls.append(source[start:index + 1])
                        cursor = index + 1
                        break
            elif state == "string":
                if char == "\\":
                    index += 1
                elif char == '"':
                    state = "code"
            elif state == "char":
                if char == "\\":
                    index += 1
                elif char == "'":
                    state = "code"
            elif state == "line-comment":
                if char in "\r\n":
                    state = "code"
            elif state == "block-comment" and char == "*" and following == "/":
                state = "code"
                index += 1
            index += 1
        else:
            failures.append(f"unterminated invocation in verifier input: {prefix}")
            break
    return calls


def method_slice(source: str, signature: str, next_signature: str | None = None) -> str:
    """Return a stable source region for ordering/conjunction checks."""
    start = source.find(signature)
    if start < 0:
        return ""
    if next_signature is None:
        return source[start:]
    end = source.find(next_signature, start + len(signature))
    return source[start:] if end < 0 else source[start:end]


def appears_in_order(source: str, *tokens: str) -> bool:
    cursor = 0
    for token in tokens:
        cursor = source.find(token, cursor)
        if cursor < 0:
            return False
        cursor += len(token)
    return True


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


def verify_application_icon() -> None:
    """Pin the supplied artwork and every Windows shell icon size we ship."""
    source = PROJECT / "Assets" / "icon.png"
    icon = PROJECT / "Assets" / "app.ico"
    project_file = PROJECT / "ZhanClawControl.csproj"
    tray_source = PROJECT / "Views" / "MainWindow.xaml.cs"
    check(source.is_file(), "Assets/icon.png is missing")
    check(icon.is_file(), "Assets/app.ico is missing")
    if not (source.is_file() and icon.is_file()):
        return

    source_bytes = source.read_bytes()
    check(sha256(source) == EXPECTED_APP_ICON_SOURCE_SHA256,
          "Assets/icon.png no longer matches the user-supplied artwork")
    check(source_bytes.startswith(b"\x89PNG\r\n\x1a\n") and len(source_bytes) >= 33,
          "Assets/icon.png is not a valid PNG")
    if source_bytes.startswith(b"\x89PNG\r\n\x1a\n") and len(source_bytes) >= 33:
        width, height, bit_depth, color_type = struct.unpack(">IIBB", source_bytes[16:26])
        check((width, height, bit_depth, color_type) == (1024, 1024, 8, 6),
              "Assets/icon.png must remain 1024x1024 8-bit RGBA")

    data = icon.read_bytes()
    check(len(data) >= 6, "Assets/app.ico is truncated")
    if len(data) < 6:
        return
    reserved, kind, count = struct.unpack_from("<HHH", data)
    check((reserved, kind) == (0, 1), "Assets/app.ico has an invalid ICO header")
    expected_sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    check(count == len(expected_sizes), "Assets/app.ico must contain nine Windows icon frames")
    check(len(data) >= 6 + count * 16, "Assets/app.ico directory is truncated")
    if len(data) < 6 + count * 16:
        return

    actual_sizes: list[int] = []
    for index in range(count):
        width_byte, height_byte, _colors, _reserved, planes, bits, length, offset = struct.unpack_from(
            "<BBBBHHII", data, 6 + index * 16
        )
        width = width_byte or 256
        height = height_byte or 256
        actual_sizes.append(width)
        check(width == height, f"Assets/app.ico frame {index} is not square")
        check(planes == 1 and bits == 32, f"Assets/app.ico frame {width}px is not 32-bit RGBA")
        check(length > 0 and offset >= 6 + count * 16 and offset + length <= len(data),
              f"Assets/app.ico frame {width}px points outside the file")
        if not (length > 0 and offset >= 6 + count * 16 and offset + length <= len(data)):
            continue
        payload = data[offset:offset + length]
        if payload.startswith(b"\x89PNG\r\n\x1a\n") and len(payload) >= 24:
            frame_width, frame_height = struct.unpack_from(">II", payload, 16)
            check((frame_width, frame_height) == (width, height),
                  f"Assets/app.ico PNG frame metadata mismatch at {width}px")
        elif len(payload) >= 16:
            header_size, frame_width, doubled_height = struct.unpack_from("<Iii", payload)
            check(header_size >= 40 and frame_width == width and abs(doubled_height) == height * 2,
                  f"Assets/app.ico DIB frame metadata mismatch at {width}px")
        else:
            check(False, f"Assets/app.ico frame {width}px is truncated")
    check(actual_sizes == expected_sizes,
          "Assets/app.ico sizes must be 16,20,24,32,40,48,64,128,256 in order")

    project_text = project_file.read_text(encoding="utf-8")
    tray_text = tray_source.read_text(encoding="utf-8")
    check("<ApplicationIcon>Assets\\app.ico</ApplicationIcon>" in project_text,
          "the executable/taskbar icon is not wired to Assets/app.ico")
    check('<Resource Include="Assets\\app.ico" />' in project_text and
          '<Resource Include="Assets\\icon.png" />' in project_text,
          "the application icon resources are not embedded")
    check("pack://application:,,,/Assets/app.ico" in tray_text,
          "the notification-area icon is not wired to Assets/app.ico")


def verify_dialog_contract() -> None:
    """Keep application-owned prompts on the themed, localized WPF dialog path."""
    dialog_dir = PROJECT / "Views" / "Dialogs"
    facade_path = dialog_dir / "AppDialog.cs"
    window_xaml = dialog_dir / "AppDialogWindow.xaml"
    window_code = dialog_dir / "AppDialogWindow.xaml.cs"
    facade = facade_path.read_text(encoding="utf-8")
    xaml = window_xaml.read_text(encoding="utf-8")
    code = window_code.read_text(encoding="utf-8")
    localization_source = STRINGS.read_text(encoding="utf-8")

    for source in PROJECT.rglob("*.cs"):
        if source == facade_path:
            continue
        text = source.read_text(encoding="utf-8")
        check("MessageBox.Show" not in text,
              f"native MessageBox found outside the fail-safe dialog facade: {source.relative_to(ROOT)}")
        check(re.search(r"\b(?:OpenFileDialog|SaveFileDialog)\b[\s\S]{0,800}?\.ShowDialog\(", text) is None,
              f"ownerless native file dialog found outside the dialog facade: {source.relative_to(ROOT)}")

    check("ShowNativeFallback" in facade and "ShowNativeActionsFallback" in facade,
          "startup/fatal dialog fallback is missing")
    check("ShowFileDialog" in facade,
          "native file dialogs must be owned through the dialog facade")
    check("PhosphorIcon" in xaml and "SurfaceBrush" in xaml and "BorderSubtleBrush" in xaml,
          "the app dialog is not using the themed Phosphor visual contract")
    check("App.Theme.Track(this)" in code,
          "the app dialog is not tracked for light/dark title-bar changes")
    check("IsDefault = action.IsDefault" in code and "CompleteAction(_escapeActionId!)" in code,
          "custom dialog Enter/Escape handling is incomplete")
    check("IsCancel = action.IsCancel" not in code,
          "custom cancel buttons must not invoke WPF DialogCancelCommand after explicit close")
    for action_key in (
        "DialogActionContinue", "DialogActionRestartNow", "DialogActionLater", "DialogActionStopAgent",
        "DialogActionRepair", "DialogActionRemove", "DialogActionRevokeAll",
        "DialogActionRestoreRestart", "DialogActionClearLog", "DialogActionExitSetup",
        "DialogActionContinueSetup", "DialogActionExit", "DialogActionDiscardExit",
        "DialogActionUninstallKeepData", "DialogActionUninstallDeleteData",
        "DialogActionUninstallContinue",
    ):
        check(action_key in localization_source,
              f"missing localized dialog action: {action_key}")

    app_sources = "\n".join(
        path.read_text(encoding="utf-8")
        for path in PROJECT.rglob("*.cs")
        if path not in (facade_path, window_code)
    )
    action_calls = balanced_calls(
        app_sources,
        ("AppDialog.ShowActionsFormat", "AppDialog.ShowActionsText", "AppDialog.ShowActions"),
    )
    check(bool(action_calls), "no application-owned custom-action dialog calls were found")
    for call in action_calls:
        check(call.count("IsDefault: true") == 1,
              "every custom-action dialog call must declare exactly one default action")
        check(call.count("IsCancel: true") == 1,
              "every custom-action dialog call must declare exactly one cancel action")
        action_constructors = (
            balanced_calls(call, ("new AppDialogAction",)) +
            balanced_calls(call, ("new",))
        )
        check(not any("AppDialogActionStyle.Danger" in action and
                      "IsDefault: true" in action for action in action_constructors),
              "a destructive dialog action is configured as default")


def verify_wpf_binding_contract() -> None:
    """Guard read-only view-model values bound to controls with TwoWay defaults."""
    wizard = (PROJECT / "Views" / "WizardWindow.xaml").read_text(encoding="utf-8")
    check(
        'IsChecked="{Binding HardenAcl, Mode=OneWay}"' in wizard,
        "Wizard HardenAcl is read-only and must use an explicit OneWay binding",
    )
    for relative in ("AuthorizationView.xaml", "StatusView.xaml", "AuditView.xaml"):
        source = (PROJECT / "Views" / relative).read_text(encoding="utf-8")
        for binding in re.findall(r'<DataGridTextColumn[^>]+Binding="\{Binding ([^}"]+)\}"', source):
            check("Mode=OneWay" in binding, f"{relative} has a display column without explicit OneWay mode: {binding}")


def verify_install_failure_experience() -> None:
    """Keep failed setup diagnosable and retryable without title-string business logic."""
    wizard_vm = (PROJECT / "ViewModels" / "WizardViewModel.cs").read_text(encoding="utf-8")
    status_vm = (PROJECT / "ViewModels" / "StatusViewModel.cs").read_text(encoding="utf-8")
    presenter = (PROJECT / "ViewModels" / "InstallStepPresenter.cs").read_text(encoding="utf-8")
    wizard_xaml = (PROJECT / "Views" / "WizardWindow.xaml").read_text(encoding="utf-8")
    task = (PROJECT / "Services" / "ScheduledTaskService.cs").read_text(encoding="utf-8")

    wrapped_failure_title_gate = (
        'step.Title is "安装中断" or "修复安装"' in presenter or
        ('title2 == "安装中断"' in presenter and 'title2 == "修复安装"' in presenter)
    )
    check(wrapped_failure_title_gate and "ClassifyUnknownFailure(step.Detail)" in presenter,
          "wrapped install/repair failures must retain the underlying stable error category")
    check('Text="{Binding TechnicalDetail, Mode=OneWay}"' in wizard_xaml and
          'IsReadOnly="True"' in wizard_xaml and 'IsReadOnlyCaretVisible="True"' in wizard_xaml,
          "wizard failures must expose selectable read-only technical details")
    check("RedactTechnicalDetail(step.Detail)" in presenter,
          "wizard technical details must pass through the redaction boundary")
    check("inspection.QueryError" in task[task.index("public async Task<ProcessResult> RegisterAsync"):],
          "post-registration inspection failures must retain QueryError/HRESULT")
    check("InstallStepKind.InstallationVerified" in wizard_vm and
          "InstallStepKind.RollbackFailed" in wizard_vm and
          "InstallStepKind.NoMutationFailure" in wizard_vm,
          "wizard completion/retry decisions must use stable step kinds")
    check("OnPropertyChanged(nameof(ShowRetryButton))" in wizard_vm or
          'OnPropertyChanged("ShowRetryButton")' in wizard_vm,
          "wizard Finished/CanRetry changes must refresh retry-button visibility")
    check("CanRetry" in wizard_vm and "RetryCommand" in wizard_vm and
          "WizardRetry" in wizard_xaml,
          "failed setup must expose an explicit safe retry path")
    check("InstallStepKind.InstallationVerified" in status_vm and
          "InstallStepKind.CleanupWarning" in status_vm and
          "DialogRepairCleanupWarning" in status_vm,
          "repair must treat verified installation plus cleanup residue as success-with-warning")


def verify_source_safety_contract() -> None:
    installer = (PROJECT / "Services" / "InstallerService.cs").read_text(encoding="utf-8")
    task = (PROJECT / "Services" / "ScheduledTaskService.cs").read_text(encoding="utf-8")
    task_inspection = (PROJECT / "Services" / "ScheduledTaskInspection.cs").read_text(encoding="utf-8")
    runtime_security = (PROJECT / "Services" / "RuntimeSecurityService.cs").read_text(encoding="utf-8")
    host = (PROJECT / "Services" / "AgentHost.cs").read_text(encoding="utf-8")
    process = (PROJECT / "Services" / "ProcessRunner.cs").read_text(encoding="utf-8")
    settings_vm = (PROJECT / "ViewModels" / "SettingsViewModel.cs").read_text(encoding="utf-8")
    payload_script = (ROOT / "scripts" / "Test-Payload.ps1").read_text(encoding="utf-8")
    compact_task = compact(task)
    compact_runtime = compact(runtime_security)
    compact_installer = compact(installer)
    check("Schedule.Service" in task, "Task Scheduler must use COM rather than localized schtasks output")
    check("RegisterTask(" in task and "StartAsync" in task and "InspectAsync" in task,
          "scheduled-task registration/start inspection boundary is incomplete")
    task_not_found = method_slice(task, "private static bool IsTaskNotFound", "private static string DescribeException")
    check("LookupTask" in task and ("GetTasks(TaskEnumHidden)" in task or "GetTasks(1)" in task) and
          "HResultFileNotFound" in task and "HResultPathNotFound" in task and
          "HResult" in task_not_found and ".Message" not in task_not_found and
          "SchedEUnknownObject" not in task,
          "scheduled-task absence must be confirmed without localized exception text")
    check("ExpectDefault" in task and "ExpectBooleanDefault" in task and
          "ReadTaskSnapshot" in task and ".Definition" in task and ".Enabled" in task and
          "EffectiveRunLevel" in task_inspection and "EffectiveEnabled" in task_inspection and
          "value.RunLevel" in task and "value.Enabled" in task and
          "XmlConvert.ToBoolean" in task and
          "http://schemas.microsoft.com/windows/2004/02/mit/task" in task and
          ("inspection.QueryFailed" in task or "scheduledTaskInspection.QueryFailed" in task),
          "Task Scheduler inspection must honor schema defaults without relaxing explicit mismatches")
    check("TaskDontAddPrincipalAce" in task and
          ("TaskCreateOrUpdate | TaskDontAddPrincipalAce" in task or
           'RegisterTask("P2P Agent", xml, 22' in compact_task) and
          "BuildTaskSecurityDescriptor(runAsSid)" in task and
          'O:BAD:P' in task and
          "TaskFullControl = 2032127" in task and "TaskReadAndExecute = 1179817" in task,
          "scheduled-task registration must apply the controlled SY/BA/runAs DACL")
    check(("OwnerSecurityInformation | DaclSecurityInformation" in task or
           "GetSecurityDescriptor(5)" in task) and
          "GetSecurityDescriptor(" in task and
          "RawSecurityDescriptor" in task and
          "DiscretionaryAclProtected" in task and
          "owner 不是 Builtin Administrators 或 SYSTEM" in task and
          "ValidateTaskSecurityDescriptor" in task,
          "scheduled-task inspection must semantically verify the effective protected DACL")
    repair_region = method_slice(installer, "public async Task<IReadOnlyList<InstallStep>> RepairAsync", "public async Task<IReadOnlyList<InstallStep>> InstallAsync")
    install_region = method_slice(installer, "public async Task<IReadOnlyList<InstallStep>> InstallAsync", "public static Task<bool> WaitForReadyAsync")
    rollback_region = method_slice(installer, "private async Task<(bool Success, string Detail)> RollbackAsync", "private static bool IsTrustedRollbackAgent")
    check("inspection.MatchesExpectedDefinition" in repair_region and
          "previousInspection.MatchesExpectedDefinition" in install_region and
          "RegisterXmlAsync(previousTaskXml" in rollback_region and
          "MatchesExpectedDefinition" in rollback_region,
          "task rollback must retain only trusted ACL snapshots and reapply the canonical DACL")
    check("RejectChildren(" in task and all(name in task for name in
          ("Principal", "LogonTrigger", "Settings", "Task")) and
          "SecurityDescriptor" in task and "任务不允许附加 Data" in task,
          "Task Scheduler inspection must reject extra execution/security semantics")
    exact_process_region = method_slice(task, "private static bool IsExactProcessRunning", "private static bool IsProcessAtPath")
    check("Process.GetProcessesByName(name)" in exact_process_region and
          "finally" in exact_process_region and ".Dispose()" in exact_process_region,
          "scheduled-task process snapshots must dispose every Process wrapper on early match")
    check("ResolveInteractiveUserSid" in runtime_security and "SidNameUse.User" in runtime_security,
          "interactive task principals must be fail-closed to actual user SIDs")
    check("ProtectAndValidateRuntimeFile" in runtime_security and
          ("ValidateExistingProtectedObject(path, allowedSids, expectedAceFlags: \"\")" in runtime_security or
           'ValidateExistingProtectedObject(path, allowedSids, "")' in runtime_security),
          "moved runtime secrets must receive and verify an exact file ACL")
    check("SetNamedSecurityInfo" in runtime_security and "SetFileSecurity(" not in runtime_security,
          "ACL writes must use SetNamedSecurityInfo and its direct Win32 error code")
    for marker in (
        "ProtectAndValidateRuntimeFile(AppPaths.SwarmKeyFile, runAsUser)",
        "ProtectAndValidateRuntimeFile(AppPaths.SwarmKeyFile, options.RunAsUser)",
    ):
        check(marker in installer, "swarm.key atomic replacement is missing its final ACL gate")
    check("ValidateExistingDataRootTrustAllowingLegacyEmbeddedSwarm" in repair_region and
          "MigrateLegacyEmbeddedSwarmAcl" in repair_region and
          "ValidateExistingDataRootTrustAllowingLegacyEmbeddedSwarm" in install_region and
          "MigrateLegacyEmbeddedSwarmAcl" in install_region and
          "GetEmbeddedSwarmKeySha256" in installer,
          "install and repair must both migrate only the current embedded legacy swarm.key")
    check("IsExactLegacyInstallRootInheritedFileAcl" in runtime_security and
          all(name in runtime_security for name in
              ("AppPaths.ConfigFile", "AppPaths.IdentityFile", "AppPaths.ApiTokenFile", "AppPaths.JournalFile")) and
          runtime_security.count("ValidateFileSha256(") >= 3 and
          ("0x1200a9" in runtime_security.lower() or "1179817" in runtime_security),
          "legacy swarm migration must constrain ACL shape, all other secrets, format, and embedded hash")
    validate_pe_region = method_slice(runtime_security, "private static void ValidatePe", "private static PayloadManifest LoadPayloadManifest")
    manifest_region = method_slice(runtime_security, "private static PayloadManifest LoadPayloadManifest", "public static void RejectReparsePoint")
    check(("requiredPeSpan = 24 + 68 + sizeof(ushort)" in runtime_security or
           ("fileStream.Length - 94" in validate_pe_region and
            "num + 24 + 68" in validate_pe_region)) and
          "PeMachine" in manifest_region and "PeSubsystem" in manifest_region and
          '"amd64"' in manifest_region and '"console"' in manifest_region,
          "PE bounds and manifest-declared shape must be enforced by the runtime verifier")
    readiness_region = method_slice(installer, "private static async Task<bool> WaitForReadyAsync", "public static async Task RestartVerifiedAsync")
    check(("using ControlApiClient client = new ControlApiClient()" in readiness_region or
           "using var client = new ControlApiClient()" in readiness_region) and
          readiness_region.find("ControlApiClient") < readiness_region.find("while"),
          "readiness polling must reuse one ControlApiClient")
    check("InstallStepKind.DeferredCleanup" in installer and "RequiresDeferredCleanup" in settings_vm,
          "deferred-cleanup business state must not depend on localized step titles")
    check("ProcessTerminationUnconfirmedException" in process,
          "unconfirmed child-process termination is not represented")
    check("ValidateRuntimeBoundary" in host and "ValidateAgentPayloadAsync" in host,
          "AgentHost is missing its runtime configuration/payload gate")
    for source in PROJECT.rglob("*.cs"):
        text = source.read_text(encoding="utf-8")
        check("RunAsync(AppPaths.AgentExe" not in text and 'new[] { "-version" }' not in text,
              f"forbidden Agent execution probe: {source.relative_to(ROOT)}")
    check("MoveFileEx" in installer and ".ps1" not in installer,
          "self-delete must not elevate a mutable temporary PowerShell script")
    check(".install-rollback-" in installer and
          ("Path.Combine(AppPaths.InstallRoot" in installer or
           'Path.Combine("C:\\\\Program Files\\\\P2PAgent"' in installer),
          "rollback backups must live in a privileged install-root directory")
    check("PrepareSecureRollbackDirectory" in installer,
          "rollback directory must receive a BA/SY-only ACL")
    check("BackupEntry" in installer and "Sha256" in installer,
          "rollback backups must be hash-pinned")
    build_workflow = (ROOT / ".github" / "workflows" / "build.yml").read_text(encoding="utf-8")
    release = (ROOT / ".github" / "workflows" / "release.yml").read_text(encoding="utf-8")
    check("CODE_SIGN_PFX_B64" in release and "Get-AuthenticodeSignature" in release,
          "formal release workflow must fail closed on outer executable signing")
    for unsupported_api in (
        "System.Reflection.PortableExecutable.PEReader",
        "[Convert]::ToHexString",
        "[Security.Cryptography.SHA256]::HashData",
        ".ExportSubjectPublicKeyInfo()",
    ):
        check(unsupported_api not in payload_script,
              f"Test-Payload.ps1 regressed to a Windows PowerShell 5.1-incompatible API: {unsupported_api}")
    check("New-Object IO.BinaryReader" in payload_script and
          "Get-SubjectPublicKeyInfo" in payload_script and
          "[Security.Cryptography.SHA256]::Create()" in payload_script,
          "Test-Payload.ps1 is missing its Windows PowerShell 5.1-compatible validation path")
    check("shell: powershell" in build_workflow and "shell: powershell" in release,
          "both build and release workflows must execute Test-Payload under Windows PowerShell 5.1")


def verify_task_maintenance_phase_contract() -> None:
    """Pin the durable Mutation -> ValidationReady controlled-start protocol."""
    app_paths = (PROJECT / "Services" / "AppPaths.cs").read_text(encoding="utf-8")
    runtime = (PROJECT / "Services" / "RuntimeSecurityService.cs").read_text(encoding="utf-8")
    task = (PROJECT / "Services" / "ScheduledTaskService.cs").read_text(encoding="utf-8")
    installer = (PROJECT / "Services" / "InstallerService.cs").read_text(encoding="utf-8")
    host = (PROJECT / "Services" / "AgentHost.cs").read_text(encoding="utf-8")
    strings = STRINGS.read_text(encoding="utf-8")

    for marker in (
        "LegacyTaskMaintenanceEnabledContent",
        "LegacyTaskMaintenanceDisabledContent",
        "TaskMaintenanceMutationEnabledContent",
        "TaskMaintenanceMutationDisabledContent",
        "TaskMaintenanceValidationReadyEnabledContent",
        "TaskMaintenanceValidationReadyDisabledContent",
    ):
        check(marker in app_paths, f"task-maintenance marker encoding is missing: {marker}")

    read_marker = method_slice(runtime, "private static (bool DesiredEnabled, TaskMaintenancePhase Phase) ReadTaskMaintenanceMarkerAtPath", "private static void PublishTaskMaintenanceMarker")
    publish_marker = method_slice(runtime, "private static void PublishTaskMaintenanceMarker", "public static MaintenanceStartAuthorization EnforceMaintenanceStartBoundaryForCurrentUser")
    enforce_host = method_slice(runtime, "public static MaintenanceStartAuthorization EnforceMaintenanceStartBoundaryForCurrentUser", "public static void CreateMaintenanceStartPermit")
    create_permit = method_slice(runtime, "public static void CreateMaintenanceStartPermit", "public static void DeleteMaintenanceStartPermitIfPresent")
    consume_permit = method_slice(runtime, "private static ParsedMaintenanceStartPermit ReadValidateAndOptionallyConsumeMaintenanceStartPermit", "private static ParsedMaintenanceStartPermit ReadValidateMaintenanceStartPermitAtPath")

    check(all(name in read_marker for name in (
        "LegacyTaskMaintenanceEnabledContent", "LegacyTaskMaintenanceDisabledContent",
        "TaskMaintenanceMutationEnabledContent", "TaskMaintenanceMutationDisabledContent",
        "TaskMaintenanceValidationReadyEnabledContent", "TaskMaintenanceValidationReadyDisabledContent",
        "TaskMaintenancePhase.Mutation", "TaskMaintenancePhase.ValidationReady")),
        "task-maintenance reader must accept legacy v1 as Mutation and both exact v2 phases")
    check("FileOptions.WriteThrough" in publish_marker and
          ("Flush(flushToDisk: true)" in publish_marker or "Flush(true)" in publish_marker) and
          "MoveFileEx(stage, AppPaths.TaskMaintenanceMarker, 9u)" in compact(publish_marker) and
          "ReadTaskMaintenanceMarkerAtPath(AppPaths.TaskMaintenanceMarker)" in publish_marker and
          "readBack.DesiredEnabled != desiredEnabled" in publish_marker and
          "readBack.Phase != phase" in publish_marker,
          "task-maintenance phase publication must use write-through atomic replace and exact readback")
    check("TaskMaintenancePhase.ValidationReady" in enforce_host and
          "consume: true" in enforce_host and
          "EnforceMaintenanceStartBoundaryForCurrentUser" in host,
          "AgentHost maintenance mode must require ValidationReady and atomically consume a permit")
    check("TaskMaintenancePhase.Mutation" in create_permit and
          "CreateMaintenanceStartPermit" in task,
          "new maintenance permits must be created only from Mutation")
    check("(!consume) ? 5u : 0u" in consume_permit and
          "SetFileInformationByHandle" in consume_permit and
          "FileDispositionInfo" in consume_permit,
          "maintenance permit consumption must hold an exclusive handle through disposition")

    controlled_start = method_slice(task, "public async Task<ProcessResult> StartAsync(bool allowTaskMaintenance", "private async Task<ProcessResult> FailMaintenanceStartAsync")
    check(appears_in_order(
              controlled_start,
              "CreateMaintenanceStartPermit",
              "MarkTaskMaintenanceValidationReady",
              "RestoreAgentExecutionForControlledStart",
              "task.Run",
              "IsAgentProcessRunning",
              "RestrictAgentExecutionForMaintenance"),
          "controlled health start must publish ValidationReady before ACL relaxation and restrict after exact Agent observation")
    failed_start = method_slice(task, "private async Task<ProcessResult> FailMaintenanceStartAsync", "public async Task<ProcessResult> StopAsync")
    check(failed_start.count("StopAsync(") >= 2 and
          appears_in_order(failed_start,
                           "StopAsync(",
                           "RestrictAgentExecutionForMaintenance",
                           "StopAsync(",
                           "RestoreTaskMaintenanceMutationPhaseIfPresent"),
          "failed controlled start must Stop, restrict, Stop again, then durably restore Mutation")

    mutation_boundary = method_slice(installer, "private async Task EstablishTaskMaintenanceMutationBoundaryAsync", "private async Task ThrowAfterStopCaptureRecoveryAsync")
    check(mutation_boundary.count("_task.StopAsync(") >= 2 and
          appears_in_order(mutation_boundary,
                           "_task.StopAsync(",
                           "RestrictAgentExecutionForMaintenance",
                           "_task.StopAsync(",
                           "RestoreTaskMaintenanceMutationPhaseIfPresent"),
          "installer mutations must close the Stop/ACL race before publishing Mutation")
    rollback = method_slice(installer, "private async Task<(bool Success, string Detail)> RollbackAsync", "private static string ScheduleSelfDelete")
    check(appears_in_order(rollback, "EstablishTaskMaintenanceMutationBoundaryAsync", "backup.RestoreAsync"),
          "rollback must restore the Mutation execution boundary before changing files")
    restored_start = method_slice(installer, "private async Task StartAndValidateRestoredRuntimeAsync", "private static async Task<(string Sha256, long Length)> HashFileBoundedAsync")
    check(appears_in_order(restored_start,
                           "StartAsync(allowTaskMaintenance: true",
                           "WaitForReadyAsync",
                           "ValidateRuntimeIdentityAsync",
                           "InspectAsync",
                           "DeleteMaintenanceStartPermitIfPresent(runtimeUser)") and
          "EstablishTaskMaintenanceMutationBoundaryAsync" in restored_start,
          "restored-runtime health commit must prove the permit absent or fall back to Mutation")
    rollback_uninstall = method_slice(installer, "public async Task<IReadOnlyList<InstallStep>> RollbackInterruptedUninstallAsync", "private static UninstallRecoveryState CreateUninstallRecoveryState")
    check("state.TaskWasPresent && !state.TaskMaintenanceMarkerPreexisted" in rollback_uninstall and
          "EnsureTaskMaintenanceMarker(state.TaskWasEnabled)" in rollback_uninstall and
          "recoveredDesiredEnabled != state.TaskWasEnabled" in rollback_uninstall and
          "recoveredPhase != RuntimeSecurityService.TaskMaintenancePhase.Mutation" in rollback_uninstall,
          "prepared uninstall rollback must reconstruct and verify an owned Mutation marker after the publication power-loss window")

    check("DeploymentTaskMaintenanceValidationReady" in installer and
          "TaskMaintenancePhase.ValidationReady" in installer and
          strings.count('"DeploymentTaskMaintenanceValidationReady"') >= 3,
          "deployment diagnostics must distinguish interrupted ValidationReady in all three languages")
    check("MoveFileEx(\"C:\\\\ProgramData\\\\P2PAgent\", AppPaths.UninstallRecoveryDataRoot, 8u)" in compact(installer),
          "DataRoot quarantine must use a write-through move")


def verify_typography_and_metrics_contract() -> None:
    """Pin the unified font, the shared control geometry, and white-on-brand text.

    These three are user-visible acceptance criteria, so a silent regression in a
    single view would otherwise only surface during Windows 11 manual testing.
    """
    controls = (PROJECT / "Themes" / "Controls.xaml").read_text(encoding="utf-8")
    light = (PROJECT / "Themes" / "Light.xaml").read_text(encoding="utf-8")
    dark = (PROJECT / "Themes" / "Dark.xaml").read_text(encoding="utf-8")
    xaml_paths = sorted(PROJECT.rglob("*.xaml"))
    views = {path: path.read_text(encoding="utf-8") for path in xaml_paths}

    # --- 1. Single font source of truth -------------------------------------
    check('<FontFamily x:Key="AppFontFamily">Microsoft YaHei UI' in controls,
          "AppFontFamily must resolve to Microsoft YaHei UI first")
    check('<FontFamily x:Key="MonoFontFamily">Microsoft YaHei UI' in controls,
          "MonoFontFamily must resolve to Microsoft YaHei UI first")
    for path, text in views.items():
        if path.name == "Controls.xaml":
            continue
        for literal in ('FontFamily="Segoe', 'FontFamily="Cascadia', 'FontFamily="Consolas'):
            check(literal not in text,
                  f"hard-coded font family outside the font tokens: {path.relative_to(ROOT)}")
        for match in re.findall(r'FontFamily="([^"]+)"', text):
            check(match in ("{StaticResource AppFontFamily}", "{StaticResource MonoFontFamily}"),
                  f"{path.relative_to(ROOT)} sets FontFamily={match} instead of a font token")
    tray = (PROJECT / "Views" / "MainWindow.xaml.cs").read_text(encoding="utf-8")
    check('TrayFontFamily = "Microsoft YaHei UI"' in tray and "ApplyTrayMenuFont" in tray,
          "the notification-area menu must use the same font family as the WPF UI")

    # --- 2. One height/radius token for every interactive control -----------
    base_style = method_slice(controls,
                              '<Style x:Key="InteractiveControlBase" TargetType="Control">',
                              "</Style>")
    check(bool(base_style), "the shared interactive-control base style is missing")
    height = re.search(r'<Setter Property="MinHeight" Value="(\d+)" />', base_style)
    check(height is not None, "the shared base style does not pin a control height")
    check('<Setter Property="FontFamily" Value="{StaticResource AppFontFamily}" />' in base_style,
          "the shared base style does not pin the application font")
    check('<CornerRadius x:Key="ControlCornerRadius"' in controls,
          "the shared control corner radius token is missing")
    interactive_blocks = {
        "Button": ('<Style TargetType="Button" BasedOn="{StaticResource InteractiveControlBase}">',
                   '<Style x:Key="AccentButton"'),
        "TextBox": ('<Style TargetType="TextBox" BasedOn="{StaticResource InteractiveControlBase}">',
                    '<Style TargetType="CheckBox">'),
        "ComboBox": ('<Style TargetType="ComboBox" BasedOn="{StaticResource InteractiveControlBase}">',
                     '<Style TargetType="TextBlock">'),
    }
    for target, (signature, terminator) in interactive_blocks.items():
        block = method_slice(controls, signature, terminator)
        check(bool(block),
              f"{target} does not inherit the shared interactive-control base style")
        check('<Setter Property="MinHeight"' not in block,
              f"{target} overrides the shared control height locally")
        check('CornerRadius="{StaticResource ControlCornerRadius}"' in block,
              f"{target} does not adopt the shared ControlCornerRadius")
    # Only the focus ring (concentric, +2px) and the 3px nav indicator may opt out.
    for match in re.findall(r'CornerRadius="([0-9][^"]*)"', controls):
        check(match in ("8", "2"),
              f"hard-coded corner radius {match} bypasses ControlCornerRadius")

    # --- 3. Brand-filled buttons always carry white label text --------------
    check('x:Key="OnBrandBrush" Color="#FFFFFF"' in light.replace('SolidColorBrush ', ''),
          "light theme OnBrandBrush must be pure white")
    check('x:Key="OnBrandBrush" Color="#FFFFFF"' in dark.replace('SolidColorBrush ', ''),
          "dark theme OnBrandBrush must be pure white")
    accent = method_slice(controls, '<Style x:Key="AccentButton"', '<Style x:Key="DangerButton"')
    check('<Setter Property="Foreground" Value="{DynamicResource OnBrandBrush}" />' in accent,
          "AccentButton must set the on-brand foreground")
    check(accent.count('Value="{DynamicResource OnBrandBrush}"') >= 4,
          "AccentButton must re-assert white text on hover, press, and disabled")
    check('Value="{DynamicResource TextDisabledBrush}"' not in accent,
          "AccentButton must never fall back to the disabled text brush on a brand fill")
    # The app-wide implicit TextBlock style otherwise wins over the button
    # Foreground on ContentPresenter-generated content text.
    check('<Style x:Key="ButtonContentText" TargetType="TextBlock">' in controls,
          "the button content-text guard style is missing")
    check(controls.count('<Style TargetType="TextBlock" BasedOn="{StaticResource ButtonContentText}" />') >= 2,
          "both button templates must scope the implicit TextBlock style")


def main() -> int:
    verify_payload()
    verify_xml()
    verify_localization()
    verify_visual_contract()
    verify_typography_and_metrics_contract()
    verify_application_icon()
    verify_dialog_contract()
    verify_wpf_binding_contract()
    verify_install_failure_experience()
    verify_source_safety_contract()
    verify_task_maintenance_phase_contract()
    if failures:
        print("source verification failed:")
        for failure in failures:
            print(f"- {failure}")
        return 1
    print("source verification passed: payload/key shape, XML, localization, icon, typography/metrics, and visual contract")
    return 0


if __name__ == "__main__":
    sys.exit(main())
