from __future__ import annotations

import base64
import json
from pathlib import Path

import pytest
from mcp.server.mcpserver.exceptions import ToolError

from revit_model_mcp.batch import register_batch
from revit_model_mcp.ssh_host import SshPowerShellHost


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
        self.discovery = []

    async def list_revit_instances(self):
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
