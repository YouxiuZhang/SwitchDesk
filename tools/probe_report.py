"""Validate and display read-only SwitchDesk diagnostic reports."""

from __future__ import annotations

import argparse
from datetime import datetime, timedelta
import json
import math
from pathlib import Path
import sys


TOP_FIELDS = frozenset({
    "schema_version", "platform", "os_version", "architecture",
    "collected_at", "read_only", "displays", "permissions",
    "capabilities", "warnings",
})
DISPLAY_FIELDS = frozenset({
    "id", "name", "builtin", "width", "height", "refresh_hz", "ddc_input",
})
DDC_FIELDS = frozenset({"status", "value", "detail"})
PERMISSION_FIELDS = frozenset({
    "screen_capture", "input_capture", "input_injection",
})
CAPABILITY_FIELDS = frozenset({
    "automatic_switch", "native_file_paste", "inactive_display_capture",
})
PERMISSION_STATES = frozenset({
    "granted", "denied", "not_checked", "not_applicable",
})
DDC_STATES = frozenset({"not_tested", "readable", "unsupported", "error"})
def _fields(value: object, path: str, required: frozenset[str], errors: list[str]) -> bool:
    if not isinstance(value, dict):
        errors.append(f"{path}: 必须是对象")
        return False
    for field in sorted(required - value.keys()):
        errors.append(f"{path}.{field}: 缺少必需字段" if path else f"{field}: 缺少必需字段")
    for field in value.keys() - required:
        safe_field = str(field).encode("utf-8", "backslashreplace").decode("utf-8")
        name = f"{path}.{safe_field}" if path else safe_field
        errors.append(f"{name}: 不允许额外字段")
    return True


def _valid_string(value: object) -> bool:
    return isinstance(value, str) and not any("\ud800" <= char <= "\udfff" for char in value)


def _nonempty_string(value: object) -> bool:
    return _valid_string(value) and bool(value.strip())


def _positive_int(value: object) -> bool:
    return type(value) is int and value > 0


def _validate_ddc(value: object, path: str, errors: list[str]) -> None:
    if not _fields(value, path, DDC_FIELDS, errors):
        return
    status = value.get("status")
    if "status" in value and (not isinstance(status, str) or status not in DDC_STATES):
        errors.append(f"{path}.status: 必须是有效的 DDC 读取状态")
    if "detail" in value and not _valid_string(value["detail"]):
        errors.append(f"{path}.detail: 必须是不含孤立代理码位的字符串")
    if "value" in value:
        code = value["value"]
        if status == "readable":
            if type(code) is not int or not 0 <= code <= 65535:
                errors.append(f"{path}.value: readable 状态必须提供 uint16 整数")
        elif code is not None:
            errors.append(f"{path}.value: 非 readable 状态必须为 null")


def _validate_display(value: object, path: str, errors: list[str]) -> None:
    if not _fields(value, path, DISPLAY_FIELDS, errors):
        return
    for field in ("id", "name"):
        if field in value and not _nonempty_string(value[field]):
            errors.append(f"{path}.{field}: 必须是非空字符串")
    if "builtin" in value and value["builtin"] is not None and type(value["builtin"]) is not bool:
        errors.append(f"{path}.builtin: 必须是布尔值或 null")
    for field in ("width", "height"):
        if field in value and not _positive_int(value[field]):
            errors.append(f"{path}.{field}: 必须是正整数")
    if "refresh_hz" in value:
        rate = value["refresh_hz"]
        valid_rate = (
            rate is None
            or (type(rate) is int and rate >= 0)
            or (type(rate) is float and math.isfinite(rate) and rate >= 0)
        )
        if not valid_rate:
            errors.append(f"{path}.refresh_hz: 必须是有限非负数或 null")
    if "ddc_input" in value:
        _validate_ddc(value["ddc_input"], f"{path}.ddc_input", errors)


def _validate_state_map(
    value: object,
    path: str,
    fields: frozenset[str],
    states: frozenset[str],
    errors: list[str],
) -> None:
    if not _fields(value, path, fields, errors):
        return
    for field in sorted(fields & value.keys()):
        if not isinstance(value[field], str) or value[field] not in states:
            errors.append(f"{path}.{field}: 状态无效")


def validate_report(report: object) -> list[str]:
    """Return contract errors with paths; never modify the report."""
    errors: list[str] = []
    if not _fields(report, "", TOP_FIELDS, errors):
        return errors

    if "schema_version" in report and (
        type(report["schema_version"]) is not int or report["schema_version"] != 1
    ):
        errors.append("schema_version: 必须是整数 1")
    if "platform" in report and report["platform"] not in ("macos", "windows"):
        errors.append("platform: 必须是 macos 或 windows")
    for field in ("os_version", "architecture"):
        if field in report and not _nonempty_string(report[field]):
            errors.append(f"{field}: 必须是非空字符串")
    if "collected_at" in report:
        timestamp = report["collected_at"]
        valid = isinstance(timestamp, str) and "T" in timestamp
        if valid:
            try:
                normalized = timestamp[:-1] + "+00:00" if timestamp.endswith("Z") else timestamp
                parsed = datetime.fromisoformat(normalized)
                valid = parsed.tzinfo is not None and parsed.utcoffset() == timedelta(0)
            except ValueError:
                valid = False
        if not valid:
            errors.append("collected_at: 必须是有效的 ISO 8601 UTC 时间")
    if "read_only" in report and report["read_only"] is not True:
        errors.append("read_only: 必须是 true")
    if "displays" in report:
        displays = report["displays"]
        if not isinstance(displays, list):
            errors.append("displays: 必须是数组")
        else:
            for index, display in enumerate(displays):
                _validate_display(display, f"displays[{index}]", errors)
    if "permissions" in report:
        _validate_state_map(
            report["permissions"], "permissions", PERMISSION_FIELDS,
            PERMISSION_STATES, errors,
        )
    if "capabilities" in report:
        _validate_state_map(
            report["capabilities"], "capabilities", CAPABILITY_FIELDS,
            frozenset({"not_tested"}), errors,
        )
    if "warnings" in report:
        warnings = report["warnings"]
        if not isinstance(warnings, list):
            errors.append("warnings: 必须是字符串数组")
        else:
            for index, warning in enumerate(warnings):
                if not _valid_string(warning):
                    errors.append(f"warnings[{index}]: 必须是不含孤立代理码位的字符串")
    return errors


def summarize_report(report: dict) -> str:
    """Summarize a valid report without asserting untested capabilities."""
    errors = validate_report(report)
    if errors:
        raise ValueError("报告不符合契约：" + "; ".join(errors))
    displays = report["displays"]
    if displays:
        display_line = f"当前进程枚举到 {len(displays)} 台显示器。"
    else:
        display_line = "当前进程未枚举到显示器。"
    readable_count = sum(display["ddc_input"]["status"] == "readable" for display in displays)
    return "\n".join([
        f"诊断环境：{report['platform']}，系统版本 {report['os_version']}。",
        display_line,
        f"DDC 输入码可读：{readable_count} 台；可读仅表示读到了当前输入码。",
        "自动切屏尚未验证。",
        f"警告：{len(report['warnings'])} 条。",
    ])


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="验证 SwitchDesk 只读诊断报告")
    parser.add_argument("report", type=Path, help="诊断报告 JSON 文件")
    parser.add_argument("--json", action="store_true", help="输出验证后的 JSON")
    args = parser.parse_args(argv)
    try:
        with args.report.open("r", encoding="utf-8") as source:
            report = json.load(source)
    except (OSError, UnicodeError, ValueError, RecursionError) as exc:
        message = str(exc).encode("utf-8", "backslashreplace").decode("utf-8")
        print(f"无法读取有效 JSON：{message}", file=sys.stderr)
        return 2
    errors = validate_report(report)
    if errors:
        for error in errors:
            print(error, file=sys.stderr)
        return 2
    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False))
    else:
        print(summarize_report(report))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
