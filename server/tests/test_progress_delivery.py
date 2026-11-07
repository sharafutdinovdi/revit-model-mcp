import asyncio
import json
from unittest.mock import MagicMock

import pytest

from revit_model_mcp.pipe_host import PipeConnection
from revit_model_mcp.revit_channel import ProgressReporter, _progress_reporter


@pytest.mark.parametrize("terminal", ["Done", "Still running, poll revit_jobs"])
def test_progress_never_decreases_across_states_ticks_and_totals(terminal):
    async def check():
        calls = []

        class Context:
            async def report_progress(self, progress, total, message):
                calls.append((progress, total, message))

        reporter = ProgressReporter(Context())

        async def flush():
            reporter.last_sent = float("-inf")
            await reporter._sender

        for state in ("queued", "waiting_revit", "running"):
            await reporter.observe({"state": state})
            await flush()
        for _ in range(4):
            reporter.last_tick -= 3
            await reporter.tick()
            await flush()
        await reporter.observe({"progress": {"steps": {"completed": 1, "total": 2}}})
        await flush()
        await reporter.observe({"state": "waiting_revit"})
        await flush()
        await reporter.observe({"progress": {"currentIndex": 0, "total": 1}})
        await flush()
        await reporter.update(1, 1, terminal)
        await flush()
        values = [call[0] for call in calls]
        assert values == sorted(values)
        assert calls[-1][:2] == (4, 4)
        known_totals = [total for _, total, _ in calls if total is not None]
        assert known_totals == [4] * len(known_totals)
        assert all(total is None or progress <= total for progress, total, _ in calls)

    asyncio.run(check())


@pytest.mark.parametrize("failure", [None, RuntimeError, asyncio.CancelledError])
def test_pipe_result_bypasses_blocked_or_failing_progress(failure):
    async def check():
        entered = asyncio.Event()
        release = asyncio.Event()
        calls = []

        class Reporter:
            async def observe(self, message):
                calls.append(message)
                entered.set()
                await release.wait()
                if failure is not None:
                    raise failure()

        reader = asyncio.StreamReader()
        connection = PipeConnection(reader, MagicMock())
        token = _progress_reporter.set(Reporter())
        try:
            future = connection.watch("job")
        finally:
            _progress_reporter.reset(token)

        def push(state, **extra):
            message = {"type": "job", "jobId": "job", "state": state, **extra}
            reader.feed_data((json.dumps(message) + "\n").encode())
            return message

        push("running")
        await asyncio.wait_for(entered.wait(), 1)
        for index in range(100):
            push("running", progress={"currentIndex": index, "total": 100})
        await asyncio.sleep(0)
        assert len(calls) == 1
        assert len(connection._progress_pending) == 1
        assert connection._progress_pending["job"]["progress"]["currentIndex"] == 99
        if failure is not None:
            release.set()
            await asyncio.sleep(0)
        terminal = push("done", result={"success": True})
        assert await asyncio.wait_for(asyncio.shield(future), 1) == terminal
        await connection.aclose()

    asyncio.run(check())


def test_notification_backlog_coalesces_and_does_not_block_result():
    async def check():
        entered = asyncio.Event()
        release = asyncio.Event()
        calls = []

        class Context:
            async def report_progress(self, progress, total, message):
                calls.append((progress, total, message))
                entered.set()
                await release.wait()

        reporter = ProgressReporter(Context())
        await reporter.update(0, 100, "Running in Revit")
        await entered.wait()
        sender = reporter._sender
        for index in range(100):
            await reporter.update(index, 100, f"Step {index}")
        result = await asyncio.wait_for(
            reporter.wait(asyncio.sleep(0, result={"success": True})), 1
        )
        await reporter.update(100, 100, "Done")
        assert result == {"success": True}
        assert reporter._sender is sender
        assert len(calls) == 1
        release.set()
        await sender
        assert calls == [(0, 100, "Running in Revit"), (100, 100, "Done")]

    asyncio.run(check())
