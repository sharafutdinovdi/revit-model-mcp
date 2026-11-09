"""Evidence budgets, portable reports and official BCF XML validation."""

import asyncio
import base64
import hashlib
import io
import json
import re
from pathlib import Path
from unittest.mock import AsyncMock
from zipfile import ZipFile

import pytest
from lxml import etree
from openpyxl import load_workbook

from revit_model_mcp import server
from revit_model_mcp.audit.bcf import write_bcf
from revit_model_mcp.audit.evidence import capture_findings
from revit_model_mcp.audit.report import build_report

SCHEMAS = Path(__file__).parent / "fixtures" / "bcf-2.1"
PNG = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a7e8AAAAASUVORK5CYII="
)


def finding(**overrides):
    return {
        "id": "BEP-mark",
        "title": "Missing <Mark>",
        "severity": "major",
        "status": "failed",
        "element_ids": [12, 13],
        "skipped": [],
        "skippedCount": 0,
        **overrides,
    }


def result(findings):
    return {
        "schema": 1,
        "model": {"title": "Tower"},
        "rules": {},
        "summary": {},
        "evaluatedAtUtc": "2026-01-01T00:00:00Z",
        "findings": findings,
        "skipped": [entry for f in findings for entry in f["skipped"]],
        "skippedCount": sum(f["skippedCount"] for f in findings),
    }


async def capture(ids, path, timeout):
    Path(path).write_bytes(PNG)
    return {
        "data": {
            "localPath": path,
            "missingIds": [],
            "components": [{"authoringToolId": "12", "ifcGuid": "0" * 22}],
            "camera": {
                "position": [10, 10, 10],
                "direction": [-1, 0, 0],
                "up": [0, 0, 1],
                "scale": 20,
            },
        },
        "skipped": [],
        "skippedCount": 0,
    }


def test_schema_bytes_match_upstream_notice():
    notice = (SCHEMAS / "NOTICE").read_text()
    entries = re.findall(r"^([a-f0-9]{64})  (\w+\.xsd)$", notice, re.M)
    assert len(entries) == 3
    for digest, name in entries:
        assert hashlib.sha256((SCHEMAS / name).read_bytes()).hexdigest() == digest


@pytest.mark.parametrize("with_image", [True, False])
def test_bcf_structure_and_official_xsd_validation(with_image):
    item = finding()
    if with_image:
        asyncio.run(capture_findings([item], capture))
    output = io.BytesIO()
    write_bcf(
        result([item, finding(status="passed"), finding(status="not_assessed", element_ids=[])]),
        output,
    )
    with ZipFile(output) as archive:
        names = archive.namelist()
        assert names.count("bcf.version") == 1
        assert len([n for n in names if n.endswith("markup.bcf")]) == 2
        schemas = {
            suffix: etree.XMLSchema(etree.parse(str(SCHEMAS / name)))
            for suffix, name in (
                (".bcf", "markup.xsd"),
                (".bcfv", "visinfo.xsd"),
                (".version", "version.xsd"),
            )
        }
        for name in names:
            for suffix, schema in schemas.items():
                if name.endswith(suffix):
                    schema.assertValid(etree.fromstring(archive.read(name)))
        markup_name = next(n for n in names if n.endswith("markup.bcf"))
        markup = etree.fromstring(archive.read(markup_name))
        assert markup.findtext("Topic/Title") == "Missing <Mark>"
        assert markup.findtext("Topic/Priority") == "major"
        view = markup.find("Viewpoints")
        prefix = markup_name.rsplit("/", 1)[0]
        visual = etree.fromstring(archive.read(prefix + "/" + view.findtext("Viewpoint")))
        components = visual.findall("Components/Selection/Component")
        assert [node.findtext("AuthoringToolId") for node in components] == ["12", "13"]
        assert all(node.findtext("OriginatingSystem") == "Autodesk Revit" for node in components)
        if with_image:
            assert components[0].get("IfcGuid") == "0" * 22
            assert components[1].get("IfcGuid") is None
            assert archive.read(prefix + "/" + view.findtext("Snapshot")) == PNG
            assert visual.findtext("OrthogonalCamera/ViewToWorldScale") == "20"
        else:
            assert "Unavailable" in markup.findtext("Topic/Description")
            assert view.find("Snapshot") is None


def test_images_embed_in_html_and_link_from_xlsx(tmp_path):
    item = finding()
    asyncio.run(capture_findings([item], capture))
    audit = tmp_path / "audit.json"
    audit.write_text(json.dumps(result([item])))
    response = build_report(audit, tmp_path / "review", "all")
    html = (tmp_path / "review.html").read_text()
    assert html.count('<img alt="Finding evidence"') == 1
    assert 'src="data:image/png;base64,' + base64.b64encode(PNG).decode() in html
    assert "Missing <Mark>" not in html
    workbook = load_workbook(tmp_path / "review.xlsx")
    link = workbook["Findings"].cell(2, 10).hyperlink.target
    assert (tmp_path / link).read_bytes() == PNG
    assert len(response["files"]) == 4
    assert response["skippedCount"] == 0
    with pytest.raises(ValueError, match="already exists"):
        build_report(audit, tmp_path / "review", "all")


def test_budget_is_severity_ordered_and_failures_are_per_finding():
    items = [finding(severity="info"), finding(severity="critical")]
    asyncio.run(capture_findings(items, capture, max_images=1))
    assert items[1]["evidence"]["status"] == "captured"
    assert "count budget" in items[0]["evidence"]["skipped"][0]
    assert items[0]["skippedCount"] == 1
    assert items[0]["status"] == "failed"

    async def broken(*args):
        raise OSError("Image unavailable")

    items = [finding(), finding()]
    asyncio.run(capture_findings(items, broken))
    assert all(f["evidence"]["skipped"] == ["Image unavailable"] for f in items)
    assert all(f["skippedCount"] == 1 for f in items)


def test_time_budget_and_partial_capture():
    async def slow(*args):
        await asyncio.sleep(1)

    items = [finding(), finding()]
    asyncio.run(capture_findings(items, slow, budget_seconds=0.01))
    assert all(f["evidence"]["status"] == "unavailable" for f in items)
    assert all("time budget" in f["evidence"]["skipped"][0] for f in items)

    async def partial(*args):
        response = await capture(*args)
        response["data"]["missingIds"] = [13]
        response["skipped"] = ["hidden geometry"]
        response["skippedCount"] = 2
        return response

    item = finding(element_ids=list(range(1, 502)))
    asyncio.run(capture_findings([item], partial))
    assert item["evidence"]["status"] == "partial"
    assert item["skippedCount"] == 4
    assert item["evidence"]["png"]


def test_three_roadmap_prompts():
    prompts = {p.name for p in asyncio.run(server.mcp.list_prompts())}
    for name in ("pre_issue_check", "weekly_coordinator_report", "subcontractor_model_acceptance"):
        assert name in prompts
        rendered = getattr(server, name)()
        assert "capture_evidence=true" in rendered
        assert "format=all" in rendered
        assert "Read-only" in rendered


@pytest.mark.parametrize("identity_changed", [False, True])
@pytest.mark.parametrize("finding_count", [1, 25])
@pytest.mark.parametrize("coordinate_base", ["shared", "internal"])
def test_live_capture_pins_document_and_persists_outcome(
    tmp_path, monkeypatch, identity_changed, finding_count, coordinate_base
):
    from revit_model_mcp.audit.evaluate import Evaluator

    responder = {"processId": 123, "documentName": "Tower", "documentPath": "Tower.rvt"}
    channel = AsyncMock()
    channel.execute.return_value = {
        "data": {
            "passport": {"title": "Tower", "versionGuid": "family"},
            "source": {"path": "Tower.rvt"},
        },
        "responder": responder,
    }
    monkeypatch.setattr(server, "channel", channel)
    monkeypatch.setenv("REVIT_MCP_AUDIT_DIR", str(tmp_path))
    monkeypatch.setattr(
        Evaluator, "evaluate", AsyncMock(return_value=[finding() for _ in range(finding_count)])
    )
    image_payload = PNG + b"\0" * 200_000

    async def execute(job, timeout, document):
        assert job.command == "capture-elements"
        assert job.payload["cameraCoordinateBase"] == coordinate_base
        assert document == "Tower.rvt"
        assert server._addressed_process_id.get() == 123
        identity = {**responder, "documentName": "Other"} if identity_changed else responder
        server._audit_response_validator.get()({"responder": identity})
        captured = await capture(job.payload["elementIds"], job.output_path, timeout)
        Path(job.output_path).write_bytes(image_payload)
        return captured

    monkeypatch.setattr(server, "_execute", execute)
    response = asyncio.run(
        server.revit_model_audit(
            rules_inline={"schema": 1, "name": "Review", "rules": []},
            compare_with="none",
            capture_evidence=True,
            camera_coordinate_base=coordinate_base,
            document="Tower",
            process_id=123,
        )
    )
    saved = json.loads(Path(response["savedTo"]).read_text())
    evidence = saved["findings"][0]["evidence"]
    encoded_response = json.dumps(response)
    assert len(encoded_response) < 40_000
    assert '"png"' not in encoded_response
    assert base64.b64encode(PNG).decode()[:40] not in encoded_response
    assert len(saved["findings"]) == finding_count
    assert all("image" not in item["evidence"] for item in saved["findings"])
    assert all(item["evidence"]["image"] is (not identity_changed) for item in response["findings"])
    if identity_changed:
        assert evidence["status"] == "unavailable"
        assert "identity changed" in evidence["skipped"][0]
        assert saved["skippedCount"] == finding_count
    else:
        assert evidence["status"] == "captured"
        assert all(
            base64.b64decode(item["evidence"]["png"]) == image_payload for item in saved["findings"]
        )


def test_invalid_images_and_idless_findings_are_reported():
    calls = []

    async def invalid(ids, path, timeout):
        calls.append(ids)
        Path(path).write_bytes(b"not a PNG")
        return {"data": {"localPath": path}}

    items = [finding(), finding(element_ids=[]), finding(status="passed")]
    asyncio.run(capture_findings(items, invalid))
    assert calls == [[12, 13]]
    assert items[0]["evidence"]["status"] == "unavailable"
    assert items[0]["skippedCount"] == 1
    assert "PNG" in items[0]["evidence"]["skipped"][0]
    assert "evidence" not in items[1] and "evidence" not in items[2]


def test_capture_without_saving_is_refused_before_model_access(monkeypatch):
    channel = AsyncMock()
    execute = AsyncMock()
    monkeypatch.setattr(server, "channel", channel)
    monkeypatch.setattr(server, "_execute", execute)
    with pytest.raises(server.ToolError, match="capture_evidence requires save=true"):
        asyncio.run(server.revit_model_audit(capture_evidence=True, save=False))
    channel.execute.assert_not_awaited()
    execute.assert_not_awaited()
