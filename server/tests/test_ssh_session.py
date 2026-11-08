import asyncio
import base64
import json
from datetime import datetime, timedelta, timezone
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

import pytest

from revit_model_mcp import package_version
from revit_model_mcp.revit_channel import Job, RevitChannel, RevitChannelError, SshUnavailableError
from revit_model_mcp.ssh_host import SshPowerShellHost


class FakeSshRunner:
    def __init__(self):
        self.commands = []
        self.submitted = {}
        self.process_started = "2026-11-01T00:00:00Z"
        self.status = {
            "processId": 42,
            "revitVersion": "2026",
            "documentTitle": "Sample",
            "documentPath": "",
            "fileChannelVersion": 2,
            "startedUtc": self.process_started,
            "instanceId": "first",
            "updatedUtc": datetime.now(timezone.utc).isoformat(),
            "addinVersion": package_version(),
            "commands": ["ping", "document-info"],
        }

    async def __call__(self, script, **kwargs):
        self.commands.append(script)
        if "$package =" in script:
            return base64.b64encode(
                json.dumps(
                    {
                        "processes": [{"processId": 42, "processStartedUtc": self.process_started}],
                        "files": [{"name": "instance_42.json", "content": json.dumps(self.status)}],
                    }
                ).encode()
            ).decode()
        if "input" in kwargs:
            self.submitted = json.loads(base64.b64decode(kwargs["input"]))
            return '{"revitRunning":true,"responses":[],"published":true}'
        if "$artifactName = $null" in script:
            response = {
                "command": self.submitted["command"],
                "success": True,
                "data": "pong",
                "correlationId": self.submitted["correlationId"],
                "responder": {"processId": 42},
            }
            return json.dumps(
                {"response": base64.b64encode(json.dumps(response).encode()).decode()}
            )
        if "$trigger =" in script:
            return "gone"
        if "Get-ChildItem" in script:
            return f"response_20261101_000000_000_{self.submitted['command']}.json"
        if "Remove-Item -LiteralPath $path" in script:
            return ""
        raise AssertionError(script)


def test_warm_call_remote_command_counts():
    async def scenario():
        host = SshPowerShellHost()
        runner = FakeSshRunner()
        host._run = runner
        channel = RevitChannel(host)
        assert (await channel.execute(Job.document_info()))["success"]
        cold = len(runner.commands)
        assert (await channel.execute(Job.document_info()))["success"]
        warm = len(runner.commands) - cold
        assert (cold, warm) == (12, 6)
        assert sum("$package =" in script for script in runner.commands[cold:]) == 1
        assert runner.submitted["command"] == "document-info"

    asyncio.run(scenario())


@pytest.mark.parametrize("change", ["process", "startedUtc", "instanceId", "expired"])
def test_session_invalidates_on_generation_or_heartbeat_change(change):
    async def scenario():
        host = SshPowerShellHost()
        runner = FakeSshRunner()
        host._run = runner
        await host.select_job(Job.ping())
        assert host._verified_instances
        if change == "expired":
            runner.status["updatedUtc"] = (
                datetime.now(timezone.utc) - timedelta(seconds=61)
            ).isoformat()
            with pytest.raises(RevitChannelError, match="not confirmed"):
                await host.select_job(Job.ping())
            assert not host._verified_instances
            runner.status["updatedUtc"] = datetime.now(timezone.utc).isoformat()
        elif change == "process":
            runner.process_started = "2026-11-02T00:00:00Z"
        else:
            runner.status[change] = "2026-11-02T00:00:00Z" if change == "startedUtc" else "second"
        before = len(runner.commands)
        await host.select_job(Job.ping())
        assert len(runner.commands) - before == 7

    asyncio.run(scenario())


def test_failed_handshake_does_not_cache_session():
    async def scenario():
        host = SshPowerShellHost()
        host._run = FakeSshRunner()
        with patch.object(
            SshPowerShellHost,
            "_handshake",
            AsyncMock(side_effect=RevitChannelError("identity mismatch")),
        ):
            with pytest.raises(RevitChannelError, match="identity mismatch"):
                await host.select_job(Job.ping())
        assert not host._verified_instances

    asyncio.run(scenario())


def test_polling_backoff_is_bounded_and_recovers_connection_loss():
    async def scenario():
        loop = SimpleNamespace(now=0.0)
        loop.time = lambda: loop.now
        delays = []

        async def advance(delay):
            delays.append(delay)
            loop.now += delay

        host = SshPowerShellHost()
        host._run = AsyncMock(
            side_effect=[SshUnavailableError("connection lost")] + ["pending"] * 7
        )
        with (
            patch("revit_model_mcp.ssh_host.asyncio.get_running_loop", return_value=loop),
            patch("revit_model_mcp.ssh_host.asyncio.sleep", side_effect=advance),
        ):
            result, attempts, elapsed = await host._poll_for_change("check", "pending", 36)
        assert result is None
        assert delays == [0.5, 1.0, 2.0, 4.0, 8.0, 10.0, 10.0, 0.5]
        assert attempts == 8
        assert elapsed == 36
        assert all(call.kwargs["timeout_seconds"] <= 5 for call in host._run.await_args_list)

    asyncio.run(scenario())
