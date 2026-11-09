"""Audit documents and immutable model history."""

import hashlib
import json
import os
import re
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path, PureWindowsPath

from revit_model_mcp.actions import env_flag, redact_model_paths
from revit_model_mcp.atomic_write import write_new_file
from revit_model_mcp.updates import state_path


def audit_directory():
    return Path(os.environ.get("REVIT_MCP_AUDIT_DIR", state_path().parent / "audits")).expanduser()


def model_identity(snapshot):
    passport = snapshot.get("passport", {})
    return {
        "title": passport.get("title", ""),
        "centralPath": passport.get("centralPath"),
        "path": snapshot.get("source", {}).get("path"),
        "versionGuid": passport.get("versionGuid"),
    }


def model_key(model):
    identity = [model.get("centralPath") or model.get("path"), model["title"]]
    if not identity[0]:
        raise ValueError("A model path is required for audit history.")
    return hashlib.sha256(json.dumps(identity, ensure_ascii=False).encode()).hexdigest()


def redact_path(path):
    return (
        PureWindowsPath(str(path)).name if env_flag("REVIT_MCP_REDACT_PATHS", False) else str(path)
    )


def _keys(result):
    return {
        (f["id"], str(element)): f
        for f in result["findings"]
        if f["status"] == "failed" or (f["status"] == "partial" and f.get("violated"))
        for element in (f["element_ids"] or [""])
    }


def compare(current, previous):
    """Match assessed findings. Incomplete evidence cannot prove a fix."""
    current["summary"]["rulesChanged"] = current["rules"]["sha256"] != previous["rules"]["sha256"]
    if not current["model"].get("versionGuid") or current["model"]["versionGuid"] != previous[
        "model"
    ].get("versionGuid"):
        current["summary"]["comparison"] = (
            "Version family is unavailable or different; deltas not assessed."
        )
        return
    old, new = _keys(previous), _keys(current)
    current_rules = {f["id"]: f for f in current["findings"]}
    previous_rules = {f["id"]: f for f in previous["findings"]}
    deltas = []
    for key in sorted(old.keys() | new.keys()):
        rule, element = key
        before, after = previous_rules.get(rule), current_rules.get(rule)
        if key in new:
            status = "unchanged" if key in old else "new"
        elif (
            before
            and after
            and before["status"] != "not_assessed"
            and after["status"] in ("passed", "failed")
            and not after.get("element_ids_truncated")
        ):
            status = "fixed"
        else:
            continue
        deltas.append(
            {
                "rule": rule,
                "element_id": element,
                "status": status,
                "severity": (new.get(key) or old[key])["severity"],
            }
        )
    current["deltas"] = deltas
    current["summary"]["byDelta"] = dict(Counter(d["status"] for d in deltas))


def _collection_time(result):
    try:
        value = datetime.fromisoformat(result["collectedAtUtc"])
        return value if value.tzinfo is not None else None
    except (KeyError, TypeError, ValueError):
        return None


def build_result(
    model,
    rules,
    findings,
    compare_with="previous",
    save=True,
    evaluated_at=None,
    *,
    history_key=None,
    collected_at=None,
):
    now = evaluated_at or datetime.now(timezone.utc).isoformat(timespec="microseconds")
    if history_key is not None and (
        not isinstance(history_key, str) or not re.fullmatch(r"[0-9a-f]{64}", history_key)
    ):
        raise ValueError("Invalid snapshot historyKey.")
    skipped = [s for f in findings for s in f["skipped"]]
    result = {
        "schema": 1,
        "model": redact_model_paths(model),
        "rules": {k: v for k, v in rules.items() if k != "rules"},
        "evaluatedAtUtc": now,
        "collectedAtUtc": now if collected_at is None else collected_at or None,
        "findings": findings,
        "summary": {
            "bySeverity": dict(Counter(f["severity"] for f in findings if f["status"] == "failed")),
            "byStatus": dict(Counter(f["status"] for f in findings)),
            "rulesChanged": False,
        },
        "skipped": skipped,
        "skippedCount": sum(f["skippedCount"] for f in findings),
    }
    result["rules"]["file"] = redact_path(result["rules"]["file"])
    result["ruleDefinitions"] = rules["rules"]
    directory = audit_directory() / (history_key or model_key(model))
    previous = None
    collected = _collection_time(result)
    if compare_with == "previous":
        for path in sorted(directory.glob("*.json"), reverse=True):
            candidate = json.loads(path.read_text(encoding="utf-8"))
            candidate_time = _collection_time(candidate)
            if (
                collected is not None
                and candidate_time is not None
                and candidate_time < collected
                and candidate["model"].get("versionGuid") == model.get("versionGuid")
                and (previous is None or candidate_time > _collection_time(previous))
            ):
                previous = candidate
    elif compare_with != "none":
        previous = json.loads(Path(compare_with).expanduser().read_text(encoding="utf-8"))
        if previous.get("model") != result["model"]:
            # Compare title and paths independently of version-family validation.
            fields = ("title", "centralPath", "path")
            if any(previous["model"].get(k) != result["model"].get(k) for k in fields):
                raise ValueError("Comparison audit belongs to another model.")
    if previous is not None:
        baseline_time = _collection_time(previous)
        if collected is not None and baseline_time is not None and baseline_time < collected:
            compare(result, previous)
        else:
            result["summary"]["comparison"] = "No earlier collection baseline; deltas not assessed."
    elif compare_with != "none":
        result["summary"]["comparison"] = "No earlier collection baseline; deltas not assessed."
    if save:
        directory.mkdir(parents=True, exist_ok=True)
        target = directory / (now.replace(":", "").replace("+0000", "Z") + ".json")
        result["savedTo"] = redact_path(target)
        payload = json.dumps(result, ensure_ascii=False, indent=2).encode()
        write_new_file(target, lambda stream: stream.write(payload))
    else:
        result["savedTo"] = None
    return result
