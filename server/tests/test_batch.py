from __future__ import annotations

import base64
import errno
import json
import os
from pathlib import Path
from unittest.mock import AsyncMock

import pytest
from mcp.server.mcpserver.exceptions import ToolError

from revit_model_mcp import batch
from revit_model_mcp.artifact_download import save_batch_artifact
from revit_model_mcp.batch import register_batch
from revit_model_mcp.ssh_host import SshPowerShellHost, _fail_dead_supervisor


class Registry:
    def __init__(self):
        self.tools = {}

    def tool(self, **_options):
        def register(function):
            self.tools[function.__name__] = function
            return function

        return register


class Host(SshPowerShellHost):
    def __init__(self):
        super().__init__("test-host")
        self.instances = [{"processId": 41}]
        self.runs = {}
        self.artifacts = {}
        self.activations = 0
        self.instance_delay = 0
        self.discovery = []

    async def list_revit_instances(self):
        if self.instance_delay:
            self.instance_delay -= 1
            if not self.instance_delay:
                self.instances = [{"processId": 91}]
        return self.instances

    async def batch_discover(self, folder, recursive):
        self.discovery.append((folder, recursive))
        return [r"C:\models\One.rvt", r"C:\models\Nested\Two.rfa"]

    async def batch_create(self, run_id, content):
        self.runs[run_id] = json.loads(content)

    async def batch_status(self, run_id):
        return self.runs[run_id]

    async def batch_cancel(self, run_id):
        self.runs[run_id]["cancelRequested"] = True

    async def batch_fetch_artifact(self, run_id, name):
        return {
            "artifactName": name,
            "artifact": base64.b64encode(self.artifacts[(run_id, name)]).decode(),
        }

    async def batch_activate_interactive(self):
        self.activations += 1
        if not self.instance_delay:
            self.instances = [{"processId": 91}]


class Channel:
    def __init__(self):
        self.jobs = []

    async def execute(self, job):
        self.jobs.append(job)
        return {"success": True}


@pytest.fixture
def anyio_backend():
    return "asyncio"


@pytest.fixture
def boundary():
    registry, host, channel = Registry(), Host(), Channel()
    register_batch(registry, lambda: host, lambda: channel)
    return registry.tools, host, channel


@pytest.mark.anyio
async def test_start_persists_inputs_and_chooses_process(boundary):
    tools, host, channel = boundary
    result = await tools["revit_batch_start"](
        folder=r"C:\models",
        recursive=True,
        years=[2026, 2027],
        parameter_rules=[{"category": "Walls", "parameter": "Mark"}],
    )
    state = host.runs[result["runId"]]
    assert result["acceptedModels"] == 2
    assert host.discovery == [(r"C:\models", True)]
    assert [model["path"] for model in state["models"]] == [
        r"C:\models\One.rvt",
        r"C:\models\Nested\Two.rfa",
    ]
    assert state["years"] == [2026, 2027]
    assert state["parameterRules"] == [{"category": "Walls", "parameter": "Mark"}]
    assert all(model["phaseTimingsMs"] == {} for model in state["models"])
    assert channel.jobs[0].payload["targetProcessId"] == 41
    assert channel.jobs[0].payload["runId"] == result["runId"]


@pytest.mark.anyio
@pytest.mark.parametrize(
    "kwargs",
    [
        {},
        {"paths": [], "folder": r"C:\models"},
        {"paths": []},
        {"paths": [" "]},
        {"paths": [r"C:\a.txt"]},
        {"paths": [r"C:\A.rvt", r"c:\a.RVT"]},
        {"paths": [r"C:\a.rvt"], "years": [2027, 2027]},
        {"paths": [r"C:\a.rvt"], "years": [2021]},
        {"paths": [r"C:\a.rvt"], "parameter_rules": [{"category": "", "parameter": "Mark"}]},
    ],
)
async def test_invalid_start_has_no_side_effects(boundary, kwargs):
    tools, host, channel = boundary
    with pytest.raises(ToolError):
        await tools["revit_batch_start"](**kwargs)
    assert not host.runs and not channel.jobs


@pytest.mark.anyio
async def test_scheduled_task_fallback_then_persisted_status_and_cancel(boundary, monkeypatch):
    tools, host, channel = boundary
    host.instances = []
    host.instance_delay = 3
    monkeypatch.setattr(batch.asyncio, "sleep", AsyncMock())
    result = await tools["revit_batch_start"](paths=[r"\\server\share\A.rvt"])
    run_id = result["runId"]
    assert host.activations == 1
    assert channel.jobs[0].payload["targetProcessId"] == 91
    assert host.runs[run_id]["models"][0]["path"] == r"\\server\share\A.rvt"
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", "1")
    assert (await tools["revit_batch_status"](run_id))["models"][0]["path"] == "A.rvt"
    assert host.runs[run_id]["models"][0]["path"] == r"\\server\share\A.rvt"
    assert (await tools["revit_batch_cancel"](run_id))["cancelRequested"] is True
    assert host.runs[run_id]["cancelRequested"] is True


@pytest.mark.anyio
async def test_status_and_fetch_expose_redacted_dialogs_without_mutating_state(
    boundary, tmp_path, monkeypatch
):
    tools, host, _channel = boundary
    run_id = (await tools["revit_batch_start"](paths=[r"C:\models\A.rvt", r"C:\models\B.rvt"]))[
        "runId"
    ]
    dialog = {
        "dialogId": "TaskDialog_Example",
        "type": "TaskDialogShowingEventArgs",
        "message": r"Review C:\models\linked\A.rvt before opening",
        "decision": "unknown",
        "result": None,
        "modelPath": r"C:\models\A.rvt",
        "phase": "open",
        "timeUtc": "2026-09-30T00:00:00Z",
    }
    host.runs[run_id]["status"] = 4
    host.runs[run_id]["models"][0].update(status=3, error="Dialog failed.", dialogs=[dialog])
    host.runs[run_id]["models"][1].update(
        status=2,
        snapshotFile="snapshot_0002.json",
        dialogs=[{**dialog, "decision": "allowed:1", "result": 1}],
    )
    host.artifacts[(run_id, "snapshot_0002.json")] = b'{"schemaVersion":1}'
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", "1")

    status = await tools["revit_batch_status"](run_id)
    fetched = await tools["revit_batch_fetch"](run_id, str(tmp_path))
    for result in (status, fetched):
        assert result["models"][0]["dialogs"][0]["modelPath"] == "A.rvt"
        assert result["models"][0]["dialogs"][0]["message"] == "Review A.rvt before opening"
        assert result["models"][1]["dialogs"][0]["decision"] == "allowed:1"
    assert fetched["models"][0]["error"] == "Dialog failed."
    assert fetched["models"][1]["status"] == "completed"
    assert host.runs[run_id]["models"][0]["dialogs"][0] == dialog
    assert json.loads(Path(fetched["localPaths"][0]).read_text()) == {"schemaVersion": 1}


@pytest.mark.anyio
async def test_fetch_copies_json_and_rejects_incomplete_or_collision(boundary, tmp_path):
    tools, host, _channel = boundary
    result = await tools["revit_batch_start"](paths=[r"C:\models\A.rvt"])
    run_id = result["runId"]
    with pytest.raises(ToolError, match="incomplete"):
        await tools["revit_batch_fetch"](run_id, str(tmp_path))
    host.runs[run_id]["models"][0].update(status=2, snapshotFile="snapshot_0001.json")
    host.runs[run_id]["status"] = 2
    host.artifacts[(run_id, "snapshot_0001.json")] = b'{"schemaVersion":1}'
    copied = await tools["revit_batch_fetch"](run_id, str(tmp_path))
    assert copied["localPaths"] == [str(tmp_path / "snapshot_0001.json")]
    assert json.loads(Path(copied["localPaths"][0]).read_text()) == {"schemaVersion": 1}
    with pytest.raises(ToolError, match="already exists"):
        await tools["revit_batch_fetch"](run_id, str(tmp_path))


@pytest.mark.anyio
async def test_fetch_redacts_snapshot_file_without_changing_host_artifact(
    boundary, tmp_path, monkeypatch
):
    tools, host, _channel = boundary
    result = await tools["revit_batch_start"](paths=[r"C:\models\A.rvt"])
    run_id = result["runId"]
    host.runs[run_id]["models"][0].update(status=2, snapshotFile="snapshot_0001.json")
    host.runs[run_id]["status"] = 2
    snapshot = {
        "schemaVersion": 1,
        "source": {"path": r"C:\models\A.rvt"},
        "passport": {"centralPath": r"\\server\share\Central.rvt"},
    }
    raw = json.dumps(snapshot).encode()
    host.artifacts[(run_id, "snapshot_0001.json")] = raw
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", "1")
    fetched = await tools["revit_batch_fetch"](run_id, str(tmp_path / "redacted"))
    assert fetched["models"] == [
        {"path": "A.rvt", "status": "completed", "localPath": fetched["localPaths"][0]}
    ]
    local = Path(fetched["localPaths"][0])
    assert json.loads(local.read_text()) == {
        "schemaVersion": 1,
        "source": {"path": "A.rvt"},
        "passport": {"centralPath": "Central.rvt"},
    }
    assert b"models" not in local.read_bytes()
    assert host.artifacts[(run_id, "snapshot_0001.json")] == raw
    host.runs[run_id]["status"] = 4
    failed_run_fetch = await tools["revit_batch_fetch"](run_id, str(tmp_path / "failed_run"))
    assert failed_run_fetch["models"][0]["status"] == "completed"
    assert (
        json.loads(Path(failed_run_fetch["localPaths"][0]).read_text())["source"]["path"] == "A.rvt"
    )
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", "0")
    unredacted = await tools["revit_batch_fetch"](run_id, str(tmp_path / "unredacted"))
    assert json.loads(Path(unredacted["localPaths"][0]).read_text()) == snapshot
    assert unredacted["models"][0]["path"] == r"C:\models\A.rvt"


@pytest.mark.anyio
async def test_fetch_cancelled_run_reports_all_models(boundary, tmp_path):
    tools, host, _channel = boundary
    run_id = (await tools["revit_batch_start"](paths=[r"C:\A.rvt", r"C:\B.rvt"]))["runId"]
    host.runs[run_id]["status"] = 3
    host.runs[run_id]["models"][0].update(status=2, snapshotFile="snapshot_0001.json")
    host.runs[run_id]["models"][1].update(status=4, error="Cancelled by user.")
    host.artifacts[(run_id, "snapshot_0001.json")] = b'{"schemaVersion":1}'
    result = await tools["revit_batch_fetch"](run_id, str(tmp_path))
    assert result["localPaths"] == [str(tmp_path / "snapshot_0001.json")]
    assert result["models"] == [
        {"path": r"C:\A.rvt", "status": "completed", "localPath": result["localPaths"][0]},
        {
            "path": r"C:\B.rvt",
            "status": "cancelled",
            "localPath": None,
            "error": "Cancelled by user.",
        },
    ]


@pytest.mark.anyio
@pytest.mark.parametrize("status", [1, "running"])
async def test_fetch_running_reports_progress(boundary, tmp_path, status):
    tools, host, _channel = boundary
    run_id = (await tools["revit_batch_start"](paths=[r"C:\A.rvt", r"C:\B.rvt"]))["runId"]
    host.runs[run_id]["status"] = status
    host.runs[run_id]["models"][0].update(status=2, snapshotFile="snapshot_0001.json")
    with pytest.raises(ToolError, match=r"1 of 2"):
        await tools["revit_batch_fetch"](run_id, str(tmp_path))


@pytest.mark.anyio
async def test_fetch_completed_model_requires_valid_snapshot_name(boundary, tmp_path):
    tools, host, _channel = boundary
    run_id = (await tools["revit_batch_start"](paths=[r"C:\A.rvt"]))["runId"]
    host.runs[run_id]["status"] = 4
    host.runs[run_id]["models"][0].update(status=2, snapshotFile="wrong.json")
    with pytest.raises(ToolError, match="snapshot"):
        await tools["revit_batch_fetch"](run_id, str(tmp_path))


@pytest.mark.anyio
async def test_fetch_failed_run_without_snapshots_reports_model_reason(boundary, tmp_path):
    tools, host, _channel = boundary
    run_id = (await tools["revit_batch_start"](paths=[r"C:\A.rvt"]))["runId"]
    host.runs[run_id]["status"] = 4
    host.runs[run_id]["models"][0].update(status=3, reason="Open failed.")
    result = await tools["revit_batch_fetch"](run_id, str(tmp_path))
    assert result["localPaths"] == []
    assert result["models"] == [
        {"path": r"C:\A.rvt", "status": "failed", "localPath": None, "reason": "Open failed."}
    ]


def test_batch_artifact_falls_back_when_hard_links_unsupported(tmp_path, monkeypatch):
    def unsupported_link(_source, _target):
        raise OSError(errno.EOPNOTSUPP, "Hard links unsupported")

    monkeypatch.setattr(os, "link", unsupported_link)
    artifact = {
        "artifactName": "snapshot_0001.json",
        "artifact": base64.b64encode(b'{"schemaVersion":1}').decode(),
    }
    target = tmp_path / "snapshot_0001.json"
    assert save_batch_artifact(artifact, str(tmp_path)) == str(target)
    assert json.loads(target.read_text()) == {"schemaVersion": 1}
    with pytest.raises(ValueError, match="already exists"):
        save_batch_artifact(artifact, str(tmp_path))
    assert target.read_bytes() == b'{"schemaVersion": 1}'
    assert list(tmp_path.iterdir()) == [target]


def test_batch_artifact_does_not_fall_back_for_other_link_errors(tmp_path, monkeypatch):
    def denied_link(_source, _target):
        raise OSError(errno.EACCES, "Permission denied")

    monkeypatch.setattr(os, "link", denied_link)
    artifact = {
        "artifactName": "snapshot_0001.json",
        "artifact": base64.b64encode(b'{"schemaVersion":1}').decode(),
    }
    with pytest.raises(ValueError, match="Cannot save batch artifact"):
        save_batch_artifact(artifact, str(tmp_path))
    assert list(tmp_path.iterdir()) == []


def test_batch_artifact_fallback_preserves_racing_destination(tmp_path, monkeypatch):
    target = tmp_path / "snapshot_0001.json"

    def unsupported_link(_source, _target):
        target.write_bytes(b"original")
        raise OSError(errno.EOPNOTSUPP, "Hard links unsupported")

    monkeypatch.setattr(os, "link", unsupported_link)
    artifact = {
        "artifactName": "snapshot_0001.json",
        "artifact": base64.b64encode(b'{"schemaVersion":1}').decode(),
    }
    with pytest.raises(ValueError, match="already exists"):
        save_batch_artifact(artifact, str(tmp_path))
    assert target.read_bytes() == b"original"
    assert list(tmp_path.iterdir()) == [target]


def test_dead_supervisor_terminalizes_only_unfinished_models():
    state = {
        "status": 1,
        "models": [
            {"status": 2, "path": "A.rvt"},
            {"status": 1, "path": "B.rvt"},
            {"status": 0, "path": "C.rvt"},
        ],
    }
    failed = _fail_dead_supervisor(state)
    assert failed["status"] == 4
    assert [model["status"] for model in failed["models"]] == [2, 3, 3]
    assert all(model["error"] for model in failed["models"][1:])
    assert state["status"] == 1
    assert state["models"][1]["status"] == 1


@pytest.mark.anyio
async def test_host_status_persists_failed_when_owned_supervisor_is_dead():
    class DeadSupervisorHost(SshPowerShellHost):
        def __init__(self):
            super().__init__("test-host")
            self.state = {
                "runId": "a" * 32,
                "status": 1,
                "supervisorProcessId": 123,
                "supervisorProcessStartedUtc": "2026-01-01T00:00:00.0000000Z",
                "models": [{"status": 1, "path": "A.rvt"}],
            }
            self.writes = []

        async def _run(self, script, **_kwargs):
            if "supervisorAlive" in script:
                return json.dumps(
                    {
                        "state": base64.b64encode(json.dumps(self.state).encode()).decode(),
                        "cancel": False,
                        "supervisorAlive": False,
                    }
                )
            self.writes.append(script)
            self.state = _fail_dead_supervisor(self.state)
            return ""

    host = DeadSupervisorHost()
    state = await host.batch_status("a" * 32)
    assert state["status"] == 4
    assert state["models"][0]["status"] == 3
    assert len(host.writes) == 1
    assert "[IO.File]::Replace" in host.writes[0]
    assert "Stop-Process" not in host.writes[0]
