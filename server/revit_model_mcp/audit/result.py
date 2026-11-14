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
        "lineageId": passport.get("lineageId"),
    }


def model_key(model):
    identity = [model.get("centralPath") or model.get("path"), model["title"]]
    if not identity[0]:
        raise ValueError("A model path is required for audit history.")
    return hashlib.sha256(json.dumps(identity, ensure_ascii=False).encode()).hexdigest()


def redact_path(path):
    if not env_flag("REVIT_MCP_REDACT_PATHS", False):
        return str(path)
    value = os.fsdecode(path) if isinstance(path, bytes) else str(path)
    path = PureWindowsPath(value)
    if path.name:
        return path.name
    return "" if path.drive.startswith("\\\\") and path.drive.strip("/\\.") else value


def local_file_error(error: Exception, *values: str | None) -> str:
    """Render OS failures from fields and hide known local paths in other diagnostics."""
    if not env_flag("REVIT_MCP_REDACT_PATHS", False):
        return str(error)
    paths = [value for value in values if value is not None]
    pending, seen, os_messages, os_errors = [error], set(), {}, []
    while pending:
        current = pending.pop()
        if id(current) in seen:
            continue
        seen.add(id(current))
        if isinstance(current, OSError):
            os_errors.append(current)
            filenames = [
                os.fsdecode(filename)
                for filename in (current.filename, current.filename2)
                if filename is not None
            ]
            paths.extend(filenames)
            detail = current.strerror or (
                current.args[0]
                if current.args and isinstance(current.args[0], str)
                else "Local file operation failed"
            )
            rendered = str(detail)
            if filenames:
                rendered += ": " + " -> ".join(redact_path(filename) for filename in filenames)
            os_messages[str(current)] = rendered
        pending.extend(
            item for item in (current.__cause__, current.__context__) if item is not None
        )
    original = str(error)
    message = os_messages[original] if isinstance(error, OSError) else original
    for source in sorted(os_messages, key=len, reverse=True):
        if source:
            message = message.replace(source, os_messages[source])

    replacements, full_paths, parents, names = {}, {}, {}, {}
    for value in paths:
        windows = PureWindowsPath(value)
        variants = {value}
        identity = value
        try:
            expanded = Path(value).expanduser()
            if expanded.is_absolute():
                resolved = expanded.resolve()
                variants.update(
                    (str(expanded), str(resolved), str(expanded.parent.resolve() / expanded.name))
                )
                identity = str(resolved)
            elif not windows.drive:
                # Relative values are only safe as an exact diagnostic payload.
                context, delimiter, payload = message.rpartition(": ")
                if delimiter and payload == value:
                    message = context + delimiter + redact_path(value)
                continue
            else:
                variants.update((str(windows), windows.as_posix()))
                identity = windows.as_posix().casefold()
        except (OSError, RuntimeError):
            if not windows.drive and not Path(value).is_absolute():
                continue
        names.setdefault(identity, windows.name)
        for variant in variants:
            path = PureWindowsPath(variant)
            if not path.name or (not path.drive and not Path(variant).is_absolute()):
                continue
            full_paths[variant] = (identity, path.name)
            replacements[variant] = path.name
            for separator in ("/", "\\"):
                parent = str(path.parent).replace("\\", separator).rstrip(separator)
                if parent:
                    replacements[parent + separator] = ""
                    parents.setdefault(parent + separator, set()).add(identity)
                    folder = path.parent.name
                    if folder:
                        for fragment in (folder + separator, separator + folder):
                            parents.setdefault(fragment, set()).add(identity)
                replacements[variant.rstrip("/\\") + separator] = ""
    for key, replacement in tuple(replacements.items()):
        if "\\" in key:
            escaped = repr(key)[1:-1]
            replacements[escaped] = replacement
            if key in full_paths:
                full_paths[escaped] = full_paths[key]
    for key, owners in tuple(parents.items()):
        if "\\" in key:
            parents.setdefault(repr(key)[1:-1], set()).update(owners)
    for key in sorted(replacements, key=len, reverse=True):
        message = message.replace(key, replacements[key])
    message = redact_model_paths({"error": message})["error"]
    if any(parent in message for parent in parents):
        context, delimiter, _ = message.partition(":")
        if not delimiter or "/" in context or "\\" in context:
            context = "Local file operation failed"
        matches = [key for key in full_paths if key in original]
        filename = ""
        if matches:
            filename = full_paths[max(matches, key=len)][1]
        else:
            owners = set().union(
                *(owners for parent, owners in parents.items() if parent in original)
            )
            if len(owners) == 1:
                filename = names[next(iter(owners))]
        detail = os_errors[0].strerror if os_errors else None
        detail = detail or "local file path withheld"
        if filename:
            detail = (detail + ": " if os_errors else "") + filename
        message = f"{context}: {detail}"
    return message


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
    if not current["model"].get("lineageId") or not previous["model"].get("lineageId"):
        current["summary"]["comparison"] = "Model lineage is unavailable; deltas not assessed."
        return
    if current["model"]["lineageId"] != previous["model"]["lineageId"]:
        current["summary"]["comparison"] = "Model lineage is different; deltas not assessed."
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
                and (previous is None or candidate_time > _collection_time(previous))
            ):
                previous = candidate
    elif compare_with != "none":
        previous = json.loads(Path(compare_with).expanduser().read_text(encoding="utf-8"))
        if previous.get("model") != result["model"]:
            # Compare title and paths independently of lineage validation.
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
