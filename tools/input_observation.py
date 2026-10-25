"""Validate and summarize read-only Windows DDC input observations."""

from __future__ import annotations

import argparse
from datetime import datetime, timedelta
import json
from pathlib import Path
import sys

try:
    from .probe_report import validate_report
except ImportError:  # Direct execution: python3 tools/input_observation.py report.json
    from probe_report import validate_report


TOP_FIELDS = frozenset({
    "schema_version", "kind", "platform", "read_only", "started_at",
    "requested_duration_seconds", "elapsed_ms", "samples",
})
SAMPLE_FIELDS = frozenset({
    "started_ms", "finished_ms", "outcome", "exit_code", "report",
})
OUTCOMES = frozenset({
    "ok", "timeout", "worker_error", "invalid_report", "cleanup_failed",
})
OUTCOME_LABELS = {
    "timeout": "工作进程超时",
    "worker_error": "工作进程失败",
    "invalid_report": "工作进程报告无效",
    "cleanup_failed": "工作进程清理失败",
}
DDC_LABELS = {
    "not_tested": "DDC 未测试",
    "unsupported": "DDC 不支持/不可读",
    "error": "DDC 读取错误/不可读",
}


def _fields(value: object, path: str, required: frozenset[str], errors: list[str]) -> bool:
    if not isinstance(value, dict):
        errors.append(f"{path}: 必须是对象" if path else "报告: 必须是对象")
        return False
    for field in sorted(required - value.keys()):
        errors.append(f"{path}.{field}: 缺少必需字段" if path else f"{field}: 缺少必需字段")
    for field in value.keys() - required:
        safe_field = str(field).encode("utf-8", "backslashreplace").decode("utf-8")
        name = f"{path}.{safe_field}" if path else safe_field
        errors.append(f"{name}: 不允许额外字段")
    return True


def _utc_timestamp(value: object) -> bool:
    if not isinstance(value, str) or "T" not in value:
        return False
    try:
        normalized = value[:-1] + "+00:00" if value.endswith("Z") else value
        parsed = datetime.fromisoformat(normalized)
        return parsed.tzinfo is not None and parsed.utcoffset() == timedelta(0)
    except ValueError:
        return False


def _nonnegative_int(value: object) -> bool:
    return type(value) is int and value >= 0


def validate_observation(report: object) -> list[str]:
    """Return exact field-path errors without changing the supplied report."""
    errors: list[str] = []
    if not _fields(report, "", TOP_FIELDS, errors):
        return errors

    if "schema_version" in report and (
        type(report["schema_version"]) is not int or report["schema_version"] != 1
    ):
        errors.append("schema_version: 必须是整数 1")
    if "kind" in report and report["kind"] != "input_observation":
        errors.append("kind: 必须是 input_observation")
    if "platform" in report and report["platform"] != "windows":
        errors.append("platform: 必须是 windows")
    if "read_only" in report and report["read_only"] is not True:
        errors.append("read_only: 必须是 true")
    if "started_at" in report and not _utc_timestamp(report["started_at"]):
        errors.append("started_at: 必须是有效的 ISO 8601 UTC 时间")
    if "requested_duration_seconds" in report and (
        type(report["requested_duration_seconds"]) is not int
        or not 15 <= report["requested_duration_seconds"] <= 120
    ):
        errors.append("requested_duration_seconds: 必须是 15..120 的整数")
    elapsed = report.get("elapsed_ms")
    if "elapsed_ms" in report and not _nonnegative_int(elapsed):
        errors.append("elapsed_ms: 必须是非负整数")

    if "samples" not in report:
        return errors
    samples = report["samples"]
    if not isinstance(samples, list) or len(samples) > 256:
        errors.append("samples: 必须是长度 0..256 的数组")
        return errors

    previous_finish = 0
    for index, item in enumerate(samples):
        path = f"samples[{index}]"
        if not _fields(item, path, SAMPLE_FIELDS, errors):
            continue
        for field in ("started_ms", "finished_ms"):
            if field in item and not _nonnegative_int(item[field]):
                errors.append(f"{path}.{field}: 必须是非负整数")
        start = item.get("started_ms")
        finish = item.get("finished_ms")
        if _nonnegative_int(start):
            if start < previous_finish:
                errors.append(f"{path}.started_ms: 不得早于上个样本结束时间")
            if _nonnegative_int(finish) and start > finish:
                errors.append(f"{path}.started_ms: 不得晚于本样本结束时间")
            if _nonnegative_int(elapsed) and start > elapsed:
                errors.append(f"{path}.started_ms: 不得晚于 elapsed_ms")
        if _nonnegative_int(finish):
            if _nonnegative_int(elapsed) and finish > elapsed:
                errors.append(f"{path}.finished_ms: 不得晚于 elapsed_ms")
            previous_finish = finish

        outcome = item.get("outcome")
        if "outcome" in item and (not isinstance(outcome, str) or outcome not in OUTCOMES):
            errors.append(f"{path}.outcome: 状态无效")
            continue
        if outcome not in OUTCOMES:
            continue
        code = item.get("exit_code")
        nested = item.get("report")
        if outcome in ("ok", "invalid_report") and (type(code) is not int or code != 0):
            errors.append(f"{path}.exit_code: {outcome} 必须为整数 0")
        elif outcome == "worker_error" and (type(code) is not int or code == 0):
            errors.append(f"{path}.exit_code: worker_error 必须为非零整数")
        elif outcome in ("timeout", "cleanup_failed") and code is not None:
            errors.append(f"{path}.exit_code: {outcome} 必须为 null")
        if outcome == "ok":
            for error in validate_report(nested):
                suffix = error if error.startswith(":") else f".{error}"
                errors.append(f"{path}.report{suffix}")
            if isinstance(nested, dict) and nested.get("platform") != "windows":
                errors.append(f"{path}.report.platform: 必须是 windows")
        elif nested is not None:
            errors.append(f"{path}.report: 非 ok 状态必须为 null")
    return errors


def summarize_observation(report: dict) -> str:
    """Describe observed values and interruptions without inferring input ownership."""
    errors = validate_observation(report)
    if errors:
        raise ValueError("观察报告不符合契约：" + "; ".join(errors))
    samples = report["samples"]
    worker_failures = sum(item["outcome"] == "worker_error" for item in samples)
    lines = [
        f"观察样本：{len(samples)} 个；工作进程失败：{worker_failures} 次。",
    ]
    if not samples:
        lines.append("未取得观测样本。")
    else:
        known_ids = {
            display["id"]
            for item in samples if item["outcome"] == "ok"
            for display in item["report"]["displays"]
        }
        for index, item in enumerate(samples, 1):
            prefix = f"样本 {index}（{item['started_ms']}–{item['finished_ms']} ms）"
            if item["outcome"] != "ok":
                lines.append(f"{prefix}：{OUTCOME_LABELS[item['outcome']]}；本次无可用 DDC 回读。")
                continue
            displays = item["report"]["displays"]
            if not displays:
                lines.append(f"{prefix}：未枚举到显示器；本次无可用 DDC 回读。")
                continue
            descriptions = []
            present_ids = set()
            for display in displays:
                display_id = display["id"]
                present_ids.add(display_id)
                ddc = display["ddc_input"]
                state = (f"DDC 输入值 {ddc['value']}"
                         if ddc["status"] == "readable" else DDC_LABELS[ddc["status"]])
                descriptions.append(f"{display_id}：{state}")
            for missing_id in sorted(known_ids - present_ids):
                descriptions.append(f"{missing_id}：本次未枚举")
            lines.append(f"{prefix}：" + "；".join(descriptions) + "。")
    lines.append("这些回读仅表示采样时读到的输入码；自动切屏尚未验证。")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="验证并摘要 SwitchDesk 只读输入观察报告")
    parser.add_argument("report", type=Path, help="观察报告 JSON 文件")
    args = parser.parse_args(argv)
    try:
        with args.report.open("r", encoding="utf-8") as source:
            report = json.load(source)
        errors = validate_observation(report)
        if errors:
            for error in errors:
                print(error, file=sys.stderr)
            return 2
        print(summarize_observation(report))
        return 0
    except (OSError, UnicodeError, ValueError, RecursionError) as exc:
        message = str(exc).encode("utf-8", "backslashreplace").decode("utf-8")
        print(f"无法读取有效观察报告：{message}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
