"""Offline multi-model audits using the shared evaluator and model history."""

import json
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path, PureWindowsPath

from revit_model_mcp.actions import redact_model_paths
from revit_model_mcp.atomic_write import write_new_file
from revit_model_mcp.audit.evaluate import Evaluator, SnapshotReadSource
from revit_model_mcp.audit.result import (
    audit_directory,
    build_result,
    model_identity,
    model_key,
    redact_path,
)


async def audit_snapshots(directory, rules, compare_with="previous", save=True, models=None):
    if compare_with not in ("previous", "none"):
        raise ValueError("Batch comparison accepts previous or none, not a single-model baseline.")
    root = Path(directory).expanduser().resolve()
    if not root.is_dir():
        raise ValueError("Snapshot directory must exist.")
    if models is None:
        models = [
            {"localPath": str(path), "status": "completed"}
            for path in sorted(root.glob("*.json"))
            if path.is_file() and not path.is_symlink()
        ]
    if not models:
        raise ValueError("No models supplied or found in the snapshot directory.")
    # Validate the inventory before writing any per-model history.
    files = set()
    for model in models:
        if not isinstance(model, dict):
            raise ValueError("Batch models must be objects from revit_batch_fetch.models.")
        local = model.get("localPath")
        if local is not None:
            path = Path(local).expanduser()
            path = path if path.is_absolute() else root / path
            if path.is_symlink() or path.resolve().parent != root:
                raise ValueError(
                    "Batch snapshot files must be direct, non-symlink directory members."
                )
            if path.resolve() in files:
                raise ValueError("A snapshot file cannot represent multiple models.")
            files.add(path.resolve())

    result = {
        "schema": 1,
        "kind": "batch_audit",
        "evaluatedAtUtc": datetime.now(timezone.utc).isoformat(timespec="microseconds"),
        "rules": {k: v for k, v in rules.items() if k != "rules"},
        "ruleDefinitions": rules["rules"],
        "models": [],
        "skipped": [],
        "skippedCount": 0,
    }
    result["rules"]["file"] = redact_path(result["rules"]["file"])
    identities = set()
    for model in models:
        row = {
            "model": redact_model_paths(
                {
                    "path": model.get("path"),
                    "title": Path(str(model.get("localPath") or "")).name,
                }
            ),
            "status": "failed",
            "audit": None,
            "summary": {"byStatus": {"not_assessed": len(rules["rules"])}, "byDelta": {}},
        }
        try:
            if model.get("status") not in (2, "completed"):
                raise ValueError(
                    str(model.get("error") or model.get("reason") or "Collection did not complete.")
                )
            local = model.get("localPath")
            if not local:
                row["status"] = "missing"
                raise ValueError("Completed model has no fetched snapshot.")
            path = Path(local).expanduser()
            path = path if path.is_absolute() else root / path
            if not path.is_file():
                row["status"] = "missing"
                raise ValueError("Fetched snapshot is missing.")
            snapshot = json.loads(path.read_text(encoding="utf-8"))
            if not isinstance(snapshot, dict):
                raise ValueError("Snapshot root must be an object.")
            if type(snapshot.get("schemaVersion")) is not int or snapshot["schemaVersion"] != 1:
                raise ValueError("Only snapshot schemaVersion 1 is supported.")
            source = SnapshotReadSource(snapshot)
            identity = model_identity(snapshot)
            key = snapshot.get("historyKey")
            if key is None:
                path_identity = identity.get("centralPath") or identity.get("path") or ""
                if PureWindowsPath(path_identity).parent == PureWindowsPath("."):
                    raise ValueError(
                        "Snapshot has no historyKey and no directory in its model path; stable history identity is unavailable."
                    )
                key = model_key(identity)
            if key in identities:
                raise ValueError("Duplicate model identity in batch snapshots.")
            identities.add(key)
            findings = await Evaluator(source).evaluate(rules["rules"])
            audit = build_result(
                identity,
                rules,
                findings,
                compare_with,
                save,
                history_key=key,
                collected_at=snapshot.get("collectedAtUtc", ""),
            )
            row.update(
                model=audit["model"], status="audited", audit=audit, summary=audit["summary"]
            )
            result["skipped"].extend(audit["skipped"])
            result["skippedCount"] += audit["skippedCount"]
        except (OSError, ValueError, TypeError, KeyError) as error:
            reason = redact_model_paths({"error": str(error)})["error"]
            row["reason"] = reason
            result["skipped"].append({"model": row["model"], "reason": reason})
            result["skippedCount"] += 1
        result["models"].append(row)
    result["summary"] = {
        "byModelStatus": dict(Counter(row["status"] for row in result["models"])),
        "byStatus": dict(
            sum((Counter(row["summary"]["byStatus"]) for row in result["models"]), Counter())
        ),
        "byDelta": dict(
            sum((Counter(row["summary"].get("byDelta", {})) for row in result["models"]), Counter())
        ),
    }
    result["savedTo"] = None
    if save:
        directory = audit_directory() / "batches"
        directory.mkdir(parents=True, exist_ok=True)
        target = directory / (result["evaluatedAtUtc"].replace(":", "") + ".json")
        result["savedTo"] = redact_path(target)
        payload = json.dumps(result, ensure_ascii=False, indent=2).encode()
        write_new_file(target, lambda stream: stream.write(payload))
    return result


def report_tables(result, single_tables):
    content = {
        "Summary": [
            ["Evaluated at UTC", "Rules", "Summary"],
            [result["evaluatedAtUtc"], result["rules"], result["summary"]],
        ],
        "Models": [
            [
                "Model",
                "Status",
                "Passed",
                "Failed",
                "Partial",
                "Not assessed",
                "New",
                "Fixed",
                "Unchanged",
                "Comparison",
                "Reason",
                "History",
            ]
        ],
    }
    for row in result["models"]:
        audit = row.get("audit")
        status, delta = row["summary"]["byStatus"], row["summary"].get("byDelta", {})
        model = row["model"]
        label = model.get("path") or model.get("centralPath") or model.get("title", "")
        content["Models"].append(
            [
                label,
                row["status"],
                *[status.get(k, 0) for k in ("passed", "failed", "partial", "not_assessed")],
                *[delta.get(k, 0) for k in ("new", "fixed", "unchanged")],
                row["summary"].get(
                    "comparison", "Compared" if audit and "deltas" in audit else "No baseline"
                ),
                row.get("reason", ""),
                audit.get("savedTo") if audit else "",
            ]
        )
        if audit:
            for title, rows in single_tables(audit).items():
                if title in ("Summary", "Rules", "Skipped"):
                    continue
                content.setdefault(title, [["Model", *rows[0]]])
                content[title].extend([[label, *values] for values in rows[1:]])
    content["Rules"] = [["Rule", "Definition"], *[[r["id"], r] for r in result["ruleDefinitions"]]]
    content["Skipped"] = [
        ["Entry"],
        *[[entry] for entry in result["skipped"]],
        ["Skipped count: " + str(result["skippedCount"])],
    ]
    return content
