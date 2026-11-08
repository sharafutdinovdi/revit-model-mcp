from __future__ import annotations

import asyncio
import json
from unittest.mock import AsyncMock

import pytest
from test_revit_channel import FakeRemoteHost

from revit_model_mcp import package_version
from revit_model_mcp.revit_channel import (
    Job,
    JobPickupStatus,
    RevitChannel,
    RevitChannelError,
    running_job,
    select_instance,
)


class Instance(FakeRemoteHost):
    def __init__(self, process_id, document_id):
        super().__init__()
        self.instance_info = {
            "processId": process_id,
            "instanceId": str(process_id),
            "revitVersion": "2026",
            "addinVersion": package_version(),
            "commands": ["execute-code", "jobs/persisted"],
            "documents": [
                {"title": "Same", "path": "", "documentId": document_id, "isActive": False},
                {
                    "title": "UI document",
                    "path": r"C:\models\UI.rvt",
                    "documentId": f"doc:ui-{process_id}",
                    "isActive": True,
                },
            ],
        }
        self.fetches = []

    async def wait_until_trigger_is_gone(self, timeout_seconds):
        raise TimeoutError

    async def fetch_job(self, job_id):
        self.fetches.append(job_id)
        return {
            "command": "execute-code",
            "success": True,
            "data": {},
            "responder": {
                "processId": self.instance_info["processId"],
                "instanceId": self.instance_info["instanceId"],
            },
        }


class Router:
    def __init__(self):
        self.instances = [Instance(41, "doc:first"), Instance(42, "doc:second")]

    async def select_job(self, job):
        selected = select_instance([item.instance_info for item in self.instances], job)
        remote = next(item for item in self.instances if item.instance_info is selected)
        return remote, job.for_process(selected["processId"])


def test_session_document_selector_and_conjunctive_process():
    router = Router()
    instances = [item.instance_info for item in router.instances]
    job = Job("execute-code", {}).for_document("doc:second")
    assert select_instance(instances, job)["processId"] == 42
    with pytest.raises(RevitChannelError, match="not open"):
        select_instance(instances, job.for_process(41))
    with pytest.raises(RevitChannelError, match="exact document ID"):
        select_instance(instances, Job("execute-code", {}).for_document("Same"))


def test_polling_keeps_process_and_rejects_restart_or_mismatch():
    async def scenario():
        router = Router()
        channel = RevitChannel(router)
        running = await channel.execute(Job("execute-code", {}).for_document("doc:second"))
        assert running["processId"] == 42
        assert running["responder"]["revitVersion"] == "2026"
        assert running["responder"]["processId"] == 42
        assert running["responder"]["instanceId"] == "42"
        assert running["responder"]["documentId"] == "doc:second"
        assert running["responder"]["activeDocument"]["documentId"] == "doc:ui-42"
        assert "process_id=42" in running["message"]
        polling = Job("jobs", {"fetchJobId": running["jobId"], "waitSeconds": 0})
        result = await channel.execute(polling)
        assert result["responder"]["processId"] == 42
        assert not router.instances[0].fetches
        with pytest.raises(RevitChannelError, match="different Revit process"):
            await channel.execute(polling.for_process(41))
        router.instances[1].instance_info["instanceId"] = "restarted"
        with pytest.raises(RevitChannelError, match="generation"):
            await channel.execute(polling)
        assert len(router.instances[1].fetches) == 1

    asyncio.run(scenario())


def test_persisted_job_rejects_foreign_generation():
    async def scenario():
        router = Router()
        original_fetch = router.instances[1].fetch_job

        async def foreign(job_id):
            result = await original_fetch(job_id)
            result["responder"]["instanceId"] = "old-generation"
            return result

        router.instances[1].fetch_job = foreign
        channel = RevitChannel(router)
        with pytest.raises(RevitChannelError, match="generation"):
            await channel.execute(
                Job("jobs", {"fetchJobId": "a" * 32, "waitSeconds": 0}).for_process(42)
            )

    asyncio.run(scenario())


def test_timeout_and_poll_receipts_replace_empty_accepted_identity():
    async def scenario():
        router = Router()
        remote = router.instances[1]
        remote.wait_until_trigger_is_gone = AsyncMock(
            return_value=JobPickupStatus(True, 0, False, 0)
        )
        remote.response_name = None
        remote.fetch_job = AsyncMock(
            return_value={
                "command": "execute-code",
                "partial": True,
                "message": "Command accepted and running.",
                "data": "accepted",
                "responder": {
                    "processId": 0,
                    "instanceId": "",
                    "revitVersion": "",
                    "documentId": None,
                    "documentName": "",
                    "documentPath": "",
                    "activeDocument": None,
                },
            }
        )
        channel = RevitChannel(router)
        running = await channel.execute(Job("execute-code", {}).for_document("doc:second"))
        polled = await channel.execute(
            Job("jobs", {"fetchJobId": running["jobId"], "waitSeconds": 0})
        )
        for receipt in (running, polled):
            assert receipt["status"] == "running"
            assert receipt["responder"] == {
                "processId": 42,
                "instanceId": "42",
                "revitVersion": "2026",
                "documentId": "doc:second",
                "documentName": "Same",
                "documentPath": "",
                "activeDocument": remote.instance_info["documents"][1],
            }
            assert "process_id=42" in receipt["message"]

    asyncio.run(scenario())


def test_running_receipt_preserves_authoritative_addin_snapshot():
    remote = Instance(42, "doc:second")
    responder = {
        "processId": 42,
        "instanceId": "42",
        "revitVersion": "2026",
        "documentId": "doc:second",
        "documentName": "Renamed target",
        "documentPath": r"C:\models\Renamed.rvt",
        "activeDocument": {"title": "Actual active", "documentId": "doc:actual-ui"},
    }
    result = running_job("a" * 32, {"responder": responder}, 42, remote.instance_info, "doc:second")
    assert result["responder"] == responder


def test_timeout_keeps_last_authoritative_snapshot_if_job_lookup_fails():
    async def scenario():
        router = Router()
        remote = router.instances[1]
        remote.wait_until_trigger_is_gone = AsyncMock(
            return_value=JobPickupStatus(True, 0, False, 0)
        )
        responder = {
            "processId": 42,
            "instanceId": "42",
            "revitVersion": "2026",
            "documentId": "doc:second",
            "documentName": "Actual target",
            "documentPath": "",
            "activeDocument": {"title": "Actual active", "documentId": "doc:actual-ui"},
        }
        remote.response_content = json.dumps(
            {
                "command": "execute-code",
                "success": False,
                "partial": True,
                "message": "Command accepted and running.",
                "data": "accepted",
                "responder": responder,
            }
        )
        remote.wait_for_new_response = AsyncMock(side_effect=["accepted.json", None])
        remote.fetch_job = AsyncMock(side_effect=RevitChannelError("lookup unavailable"))
        running = await RevitChannel(router).execute(
            Job("execute-code", {}).for_document("doc:second")
        )
        assert running["status"] == "running"
        assert running["responder"] == responder

    asyncio.run(scenario())


@pytest.mark.parametrize("command", ["process-models", "open-document", "new-document"])
def test_queued_process_operation_does_not_claim_the_active_document_as_its_target(command):
    remote = Instance(42, "doc:second")
    receipt = running_job("a" * 32, process_id=42, identity=remote.instance_info, command=command)
    assert receipt["responder"]["documentId"] is None
    assert receipt["responder"]["documentName"] == ""
    assert receipt["responder"]["documentPath"] == ""
    assert receipt["responder"]["activeDocument"]["documentId"] == "doc:ui-42"


@pytest.mark.parametrize("document", ["Same", "Same.rvt", r"C:\models\Same.rvt", None])
@pytest.mark.parametrize("responding", [True, False])
def test_actions_refuse_text_target_even_when_only_one_process_matches(document, responding):
    router = Router()
    instances = [item.instance_info for item in router.instances]
    instances[0]["documents"][0]["path"] = r"C:\models\Same.rvt"
    instances[1] = {"processId": 42, "pluginResponding": responding}
    with pytest.raises(RevitChannelError, match=r"doc:.*revit_documents.*process_id"):
        select_instance(instances, Job("execute-code", {}).for_document(document))


@pytest.mark.parametrize(
    "command", ["execute-code", "select", "save-tool", "set-read-only", "jobs"]
)
def test_exact_action_targets_and_read_title_selection(command):
    router = Router()
    instances = [item.instance_info for item in router.instances]
    instances[1] = {"processId": 42, "pluginResponding": False}
    job = Job(command, {"requestCancellation": True})
    assert select_instance(instances, job.for_document("doc:first"))["processId"] == 41
    assert select_instance(instances, job.for_document("Same").for_process(41))["processId"] == 41
    assert select_instance(instances[:1], job.for_document("Same"))["processId"] == 41
    assert select_instance(instances, Job.document_info().for_document("Same"))["processId"] == 41


def test_action_wrapper_refuses_title_before_sending_and_adds_identity():
    from mcp.server.mcpserver.exceptions import ToolError

    from revit_model_mcp.actions import _send_action

    async def scenario():
        router = Router()
        host = AsyncMock()
        host.list_revit_instances.return_value = [
            router.instances[0].instance_info,
            {"processId": 42, "pluginResponding": False},
        ]
        execute = AsyncMock(return_value={"success": True, "data": {"count": 1}})
        with pytest.raises(ToolError, match="exact document ID"):
            await _send_action(execute, lambda: host, "select", document="Same")
        execute.assert_not_awaited()
        for target in ({"document": "doc:first"}, {"document": "Same", "process_id": 41}):
            result = await _send_action(execute, lambda: host, "select", **target)
            assert result == {
                "success": True,
                "data": {"count": 1},
                "processId": 41,
                "revitVersion": "2026",
            }
        host.list_revit_instances.return_value = [router.instances[0].instance_info]
        await _send_action(execute, lambda: host, "select", document="Same")
        assert execute.await_count == 3

    asyncio.run(scenario())


def test_pipe_action_selection_counts_processes_without_a_heartbeat():
    from revit_model_mcp.pipe_host import LocalPipeHost, PipeJobHost

    async def scenario():
        instance = Instance(41, "doc:first").instance_info
        instance["protocols"] = ["pipe/1"]
        fallback = AsyncMock()
        fallback._discover_instances.return_value = [
            instance,
            {"processId": 42, "pluginResponding": False},
        ]
        host = LocalPipeHost(fallback)
        host.heartbeats = lambda: [instance]
        host.connection = AsyncMock()
        with pytest.raises(RevitChannelError, match="exact document ID"):
            await host.select_job(Job("execute-code", {}).for_document("Same"))
        host.connection.assert_not_awaited()
        for job in (
            Job("execute-code", {}).for_document("doc:first"),
            Job("execute-code", {}).for_document("Same").for_process(41),
        ):
            selected, addressed = await host.select_job(job)
            assert isinstance(selected, PipeJobHost)
            assert addressed.payload["targetProcessId"] == 41
        execute = AsyncMock(return_value={"success": True})
        from revit_model_mcp.actions import _send_action

        with pytest.raises(Exception, match="exact document ID"):
            await _send_action(execute, lambda: host, "select", document="Same")
        execute.assert_not_awaited()

    asyncio.run(scenario())


@pytest.mark.parametrize("command", ["execute-code", "select", "save-tool", "set-read-only"])
@pytest.mark.parametrize("success", [True, False])
def test_channel_action_responses_include_identity_without_changing_payload(command, success):
    from unittest.mock import patch

    async def scenario():
        remote = Instance(41, "doc:first")
        remote.instance_info["commands"].append(command)
        response = {"success": success, "data": {"existing": True}, "summary": "Existing"}
        with patch.object(RevitChannel, "_execute_serial", AsyncMock(return_value=response)):
            result = await RevitChannel(remote).execute(Job(command, {}).for_process(41))
        assert result == {
            "success": success,
            "data": {"existing": True},
            "summary": "Existing",
            "processId": 41,
            "revitVersion": "2026",
        }

    asyncio.run(scenario())


def test_read_response_is_unchanged():
    from unittest.mock import patch

    async def scenario():
        remote = Instance(41, "doc:first")
        remote.instance_info["commands"].append("document-info")
        response = {"success": True, "data": {"existing": True}}
        with patch.object(RevitChannel, "_execute_serial", AsyncMock(return_value=response)):
            result = await RevitChannel(remote).execute(Job.document_info())
        assert result == {"success": True, "data": {"existing": True}}

    asyncio.run(scenario())
