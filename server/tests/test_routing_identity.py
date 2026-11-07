from __future__ import annotations

import asyncio

import pytest
from test_revit_channel import FakeRemoteHost

from revit_model_mcp import package_version
from revit_model_mcp.revit_channel import Job, RevitChannel, RevitChannelError, select_instance


class Instance(FakeRemoteHost):
    def __init__(self, process_id, document_id):
        super().__init__()
        self.instance_info = {
            "processId": process_id,
            "instanceId": str(process_id),
            "revitVersion": "2026",
            "addinVersion": package_version(),
            "commands": ["execute-code", "jobs/persisted"],
            "documents": [{"title": "Same", "path": "", "documentId": document_id}],
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
    with pytest.raises(RevitChannelError, match="ambiguous"):
        select_instance(instances, Job("execute-code", {}).for_document("Same"))


def test_polling_keeps_process_and_rejects_restart_or_mismatch():
    async def scenario():
        router = Router()
        channel = RevitChannel(router)
        running = await channel.execute(Job("execute-code", {}).for_document("doc:second"))
        assert running["processId"] == 42
        assert running["responder"]["revitVersion"] == "2026"
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
