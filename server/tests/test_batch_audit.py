"""Offline batch audits retain inventory and reuse per-model evidence history."""

import asyncio
import copy
import json
from pathlib import Path
from unittest.mock import AsyncMock, patch

import pytest
from mcp.server.mcpserver.exceptions import ToolError
from openpyxl import load_workbook

from revit_model_mcp import server
from revit_model_mcp.audit.batch import audit_snapshots
from revit_model_mcp.audit.report import build_report
from revit_model_mcp.audit.result import build_result, model_identity, model_key
from revit_model_mcp.audit.rules import load_rules

FIXTURE = Path(__file__).parent / "fixtures" / "snapshots" / "current" / "model-a.json"


def snapshot(name="A", ids=(1, 2), collected="2026-09-30T12:00:00Z"):
    value = json.loads(FIXTURE.read_text())
    value["source"]["path"] = "models/" + name + ".rvt"
    value["collectedAtUtc"] = collected
    value["passport"].update(title=name, centralPath=None, versionGuid="family-" + name)
    value.update(skipped=[], skippedCount=0)
    value["parameterFill"]["rows"] = [
        {
            "category": "Doors",
            "parameter": "Mark",
            "total": 10,
            "filled": 10 - len(ids),
            "sampleEmptyIds": list(ids),
            "sampleMissingIds": [],
        }
    ]
    return value


def rules():
    return load_rules(
        inline={
            "schema": 1,
            "name": "Review",
            "rules": [
                {
                    "id": "marks",
                    "title": "Marks",
                    "severity": "major",
                    "scope": {"categories": [{"names": ["Doors"]}]},
                    "check": {"type": "parameter_filled", "parameter": {"names": ["Mark"]}},
                },
                {
                    "id": "sheets",
                    "title": "Sheets",
                    "severity": "minor",
                    "check": {"type": "sheets", "metric": "missing_title_block"},
                },
            ],
        }
    )


def put(directory, name, value):
    path = directory / name
    path.write_text(json.dumps(value))
    return path


def run(directory, **kwargs):
    return asyncio.run(audit_snapshots(str(directory), rules(), **kwargs))


def test_two_runs_store_history_and_compare_each_model(tmp_path, monkeypatch):
    history, current = tmp_path / "history", tmp_path / "current"
    current.mkdir()
    monkeypatch.setenv("REVIT_MCP_AUDIT_DIR", str(history))
    a, b = snapshot(), snapshot("B", (4,))
    put(current, "a.json", a)
    put(current, "b.json", b)
    first = run(current)
    assert first["summary"]["byStatus"] == {"failed": 2, "not_assessed": 2}
    assert first["summary"]["byDelta"] == {}
    a = snapshot(ids=(2, 3), collected="2026-10-01T12:00:00Z")
    b = snapshot("B", (), collected="2026-10-01T12:00:00Z")
    put(current, "a.json", a)
    put(current, "b.json", b)
    second = run(current)
    assert second["summary"]["byStatus"] == {"failed": 1, "passed": 1, "not_assessed": 2}
    assert second["models"][0]["summary"]["byDelta"] == {"fixed": 1, "unchanged": 1, "new": 1}
    assert second["models"][1]["summary"]["byDelta"] == {"fixed": 1}
    assert second["summary"]["byDelta"] == {"fixed": 2, "unchanged": 1, "new": 1}
    for value in (a, b):
        assert len(list((history / model_key(model_identity(value))).glob("*.json"))) == 2
    assert len(list((history / "batches").glob("*.json"))) == 2
    files = build_report(second["savedTo"], str(tmp_path / "review"))
    assert len(files["files"]) == 2
    book = load_workbook(tmp_path / "review.xlsx")
    assert book["Models"].max_row == 3
    assert book["Models"].cell(2, 6).value == "1"
    assert book["Deltas"].max_row == 5
    assert book["Findings"].max_row == 5
    html = (tmp_path / "review.html").read_text()
    assert "Not assessed" in html and "fixed" in html and "A.rvt" in html
    assert "<script" not in html
    with pytest.raises(ValueError, match="already exists"):
        build_report(second["savedTo"], str(tmp_path / "review"))


def test_inventory_keeps_failed_missing_invalid_and_cancelled_models(tmp_path):
    put(tmp_path, "good.json", snapshot())
    (tmp_path / "invalid.json").write_text("not json")
    models = [
        {"path": "A.rvt", "status": "completed", "localPath": "good.json"},
        {"path": "B.rvt", "status": "failed", "error": "Open failed"},
        {"path": "C.rvt", "status": 2, "localPath": "absent.json"},
        {"path": "D.rvt", "status": "completed", "localPath": "invalid.json"},
        {"path": "E.rvt", "status": "completed", "localPath": None},
        {"path": "F.rvt", "status": "cancelled", "reason": "Cancelled"},
    ]
    result = run(tmp_path, save=False, models=models)
    assert len(result["models"]) == 6
    assert result["summary"]["byModelStatus"] == {"audited": 1, "failed": 3, "missing": 2}
    assert result["summary"]["byStatus"]["not_assessed"] == 11
    assert result["skippedCount"] == 5
    assert result["savedTo"] is None
    assert result["models"][1]["reason"] == "Open failed"
    assert result["models"][5]["reason"] == "Cancelled"
    target = put(tmp_path, "result.json", result)
    build_report(str(target), str(tmp_path / "inventory"))
    assert load_workbook(tmp_path / "inventory.xlsx")["Models"].max_row == 7
    assert "Cancelled" in (tmp_path / "inventory.html").read_text()


def test_unavailable_and_incomplete_evidence_never_proves_fixes(tmp_path, monkeypatch):
    monkeypatch.setenv("REVIT_MCP_AUDIT_DIR", str(tmp_path / "history"))
    put(tmp_path, "a.json", snapshot())
    run(tmp_path)
    value = snapshot(ids=(), collected="2026-10-01T12:00:00Z")
    value.update(skipped=[{"what": "closed workset", "reason": "not loaded"}], skippedCount=1)
    put(tmp_path, "a.json", value)
    result = run(tmp_path)
    assert result["models"][0]["audit"]["findings"][0]["status"] == "partial"
    assert result["summary"]["byDelta"] == {}
    del value["parameterFill"]
    value["collectedAtUtc"] = "2026-10-02T12:00:00Z"
    put(tmp_path, "a.json", value)
    result = run(tmp_path)
    assert result["summary"]["byStatus"] == {"not_assessed": 2}
    assert result["summary"]["byDelta"] == {}


def test_equal_titles_do_not_merge_models_and_version_changes_have_no_delta(tmp_path, monkeypatch):
    monkeypatch.setenv("REVIT_MCP_AUDIT_DIR", str(tmp_path / "history"))
    a, b = snapshot(), snapshot("B")
    b["passport"]["title"] = "A"
    put(tmp_path, "a.json", a)
    put(tmp_path, "b.json", b)
    run(tmp_path)
    a["passport"]["versionGuid"] = "another-family"
    a["collectedAtUtc"] = b["collectedAtUtc"] = "2026-10-01T12:00:00Z"
    put(tmp_path, "b.json", b)
    put(tmp_path, "a.json", a)
    result = run(tmp_path)
    assert "deltas" not in result["models"][0]["audit"]
    assert result["models"][1]["summary"]["byDelta"] == {"unchanged": 2}


def test_redaction_covers_batch_rows_errors_and_history(tmp_path, monkeypatch):
    monkeypatch.setenv("REVIT_MCP_AUDIT_DIR", str(tmp_path / "history"))
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", "1")
    value = snapshot()
    value["source"]["path"] = r"C:\private\A.rvt"
    put(tmp_path, "a.json", value)
    result = run(
        tmp_path,
        models=[
            {"path": r"C:\private\A.rvt", "status": 2, "localPath": "a.json"},
            {
                "path": r"C:\private\B.rvt",
                "status": "failed",
                "error": r"Cannot open C:\private\B.rvt",
            },
        ],
    )
    assert "private" not in json.dumps(result)
    assert str(tmp_path) not in json.dumps(result)
    for path in (tmp_path / "history").rglob("*.json"):
        assert "private" not in path.read_text()


@pytest.mark.parametrize("version", [True, 2, None])
def test_bad_snapshot_schema_is_reported_per_model(tmp_path, version):
    value = snapshot()
    value["schemaVersion"] = version
    put(tmp_path, "a.json", value)
    result = run(tmp_path, save=False)
    assert result["models"][0]["status"] == "failed"
    assert result["skippedCount"] == 1


def test_inventory_validation_and_empty_directory(tmp_path):
    with pytest.raises(ValueError, match="No models"):
        run(tmp_path, save=False)
    put(tmp_path, "a.json", snapshot())
    with pytest.raises(ValueError, match="previous or none"):
        run(tmp_path, compare_with="baseline.json")
    with pytest.raises(ValueError, match="multiple models"):
        run(tmp_path, models=[{"localPath": "a.json"}] * 2)
    with pytest.raises(ValueError, match="direct"):
        run(tmp_path, models=[{"localPath": "../outside.json"}])
    with pytest.raises(ValueError, match="objects"):
        run(tmp_path, models=[None])


def test_tool_uses_no_transport_and_report_tool_accepts_batch(tmp_path, monkeypatch):
    monkeypatch.setenv("REVIT_MCP_AUDIT_DIR", str(tmp_path / "history"))
    put(tmp_path, "a.json", snapshot())
    loaded = rules()
    inline = {"schema": 1, "name": "Review", "rules": copy.deepcopy(loaded["rules"])}
    for rule in inline["rules"]:
        rule.pop("pack", None)
    with patch.object(server.channel, "execute", new_callable=AsyncMock) as execute:
        result = asyncio.run(
            server.revit_model_audit(snapshots_dir=str(tmp_path), rules_inline=inline)
        )
        files = server.revit_audit_report(result["savedTo"], str(tmp_path / "review"))
        execute.assert_not_called()
    assert len(files["files"]) == 2
    with pytest.raises(ToolError, match="requires snapshots_dir"):
        asyncio.run(server.revit_model_audit(batch_models=[]))
    with pytest.raises(ToolError, match="live document"):
        asyncio.run(server.revit_model_audit(snapshots_dir=str(tmp_path), document="A"))


def test_basename_only_snapshot_without_history_key_fails(tmp_path):
    value = snapshot()
    value["source"]["path"] = "A.rvt"
    put(tmp_path, "a.json", value)
    result = run(tmp_path, save=False)
    assert result["models"][0]["status"] == "failed"
    assert "no historyKey" in result["models"][0]["reason"]
    assert result["summary"]["byStatus"] == {"not_assessed": 2}


def test_offline_collection_predating_live_audit_cannot_prove_fixes(tmp_path, monkeypatch):
    monkeypatch.setenv("REVIT_MCP_AUDIT_DIR", str(tmp_path / "history"))
    value = snapshot()
    put(tmp_path, "a.json", value)
    findings = run(tmp_path, save=False)["models"][0]["audit"]["findings"]
    build_result(
        model_identity(value),
        rules(),
        findings,
        collected_at="2026-10-02T12:00:00Z",
        evaluated_at="2026-10-02T12:01:00Z",
    )
    put(tmp_path, "a.json", snapshot(ids=()))
    result = run(tmp_path)
    audit = result["models"][0]["audit"]
    assert audit["collectedAtUtc"] == value["collectedAtUtc"]
    assert "deltas" not in audit
    assert "not assessed" in audit["summary"]["comparison"]


def test_history_selects_latest_earlier_collection_not_latest_evaluation(tmp_path, monkeypatch):
    monkeypatch.setenv("REVIT_MCP_AUDIT_DIR", str(tmp_path / "history"))
    value = snapshot()
    put(tmp_path, "a.json", value)
    findings = run(tmp_path, save=False)["models"][0]["audit"]["findings"]
    build_result(
        model_identity(value),
        rules(),
        findings,
        collected_at="2026-09-29T12:00:00Z",
        evaluated_at="2026-10-02T12:00:00Z",
    )
    build_result(
        model_identity(value),
        rules(),
        findings,
        collected_at="2026-10-01T12:00:00Z",
        evaluated_at="2026-10-03T12:00:00Z",
    )
    older = copy.deepcopy(findings)
    older[0]["element_ids"] = [9]
    build_result(
        model_identity(value),
        rules(),
        older,
        collected_at="2026-09-28T12:00:00Z",
        evaluated_at="2026-10-04T12:00:00Z",
    )
    put(tmp_path, "a.json", snapshot(ids=(2, 3)))
    result = run(tmp_path)
    assert result["models"][0]["summary"]["byDelta"] == {"fixed": 1, "unchanged": 1, "new": 1}


def test_equal_or_missing_collection_time_leaves_deltas_unassessed(tmp_path, monkeypatch):
    monkeypatch.setenv("REVIT_MCP_AUDIT_DIR", str(tmp_path / "history"))
    put(tmp_path, "a.json", snapshot())
    run(tmp_path)
    value = snapshot(ids=())
    put(tmp_path, "a.json", value)
    assert "deltas" not in run(tmp_path)["models"][0]["audit"]
    del value["collectedAtUtc"]
    put(tmp_path, "a.json", value)
    audit = run(tmp_path)["models"][0]["audit"]
    assert audit["collectedAtUtc"] is None
    assert "deltas" not in audit
    assert "not assessed" in audit["summary"]["comparison"]
