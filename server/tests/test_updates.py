import io
import json
from datetime import datetime, timedelta, timezone
from unittest.mock import patch

import pytest

from revit_model_mcp import package_version, updates
from revit_model_mcp.revit_channel import (
    RevitChannelError,
    check_addin_compatibility,
)


@pytest.mark.parametrize(
    ("candidate", "current", "expected"),
    [
        ("0.7.0", "0.6.0", True),
        ("0.6.0", "0.6", False),
        ("1!0.1", "0.99", True),
        ("0.6.0.post1", "0.6.0", True),
        ("0.7.0rc1", "0.6.0", False),
        ("0.7.0.dev1", "0.6.0", False),
    ],
)
def test_stable_version_comparison(candidate, current, expected):
    assert updates.newer_stable(candidate, current) is expected


def test_update_check_writes_timestamp_before_spawn_and_throttles(tmp_path, monkeypatch):
    monkeypatch.delenv("REVIT_MCP_NO_UPDATE_CHECK", raising=False)
    path = tmp_path / "update.json"
    now = datetime(2026, 9, 29, tzinfo=timezone.utc)
    with (
        patch.object(updates.subprocess, "Popen") as spawn,
        patch.object(updates.threading, "Thread") as thread,
    ):
        spawn.side_effect = lambda *args, **kwargs: (
            updates.read_state(path)["lastChecked"] == now.isoformat()
        )
        updates.check_for_updates(path, now)
        updates.check_for_updates(path, now + timedelta(hours=23))
        assert spawn.call_count == 1
        assert thread.call_count == 1
        updates.check_for_updates(path, now + timedelta(hours=24))
        assert spawn.call_count == 2
    assert "--prerelease=disallow" in spawn.call_args.args[0]
    assert spawn.call_args.kwargs["stdin"] == updates.subprocess.DEVNULL
    assert spawn.call_args.kwargs["env"]["UV_HTTP_TIMEOUT"] == "10"


def test_update_opt_out_does_not_spawn_or_request(tmp_path, monkeypatch):
    monkeypatch.setenv("REVIT_MCP_NO_UPDATE_CHECK", "1")
    with (
        patch.object(updates.subprocess, "Popen") as spawn,
        patch.object(updates.urllib.request, "urlopen") as request,
    ):
        updates.check_for_updates(tmp_path / "update.json")
    spawn.assert_not_called()
    request.assert_not_called()
    assert updates.update_status()["updateCheck"] == "disabled"


def test_windows_refresh_starts_hidden_process(tmp_path, monkeypatch):
    monkeypatch.delenv("REVIT_MCP_NO_UPDATE_CHECK", raising=False)
    with (
        patch.object(updates.os, "name", "nt"),
        patch.object(updates.subprocess, "CREATE_NO_WINDOW", 0x08000000, create=True),
        patch.object(updates.subprocess, "CREATE_NEW_PROCESS_GROUP", 0x00000200, create=True),
        patch.object(updates.subprocess, "Popen") as spawn,
        patch.object(updates.threading, "Thread"),
    ):
        updates.check_for_updates(tmp_path / "update.json")
    spawn.assert_called_once()
    assert spawn.call_args.kwargs["creationflags"] == 0x08000200
    assert spawn.call_args.kwargs["start_new_session"] is False
    assert spawn.call_args.kwargs["close_fds"] is True
    for stream in ("stdin", "stdout", "stderr"):
        assert spawn.call_args.kwargs[stream] == updates.subprocess.DEVNULL


def test_pypi_lookup_records_latest_stable_even_when_prerelease_is_newest(tmp_path):
    path = tmp_path / "update.json"
    package = {
        "releases": {
            "0.6.0": [{"yanked": False}],
            "0.7.0rc1": [{"yanked": False}],
            "0.7.0": [{"yanked": False}],
            "0.8.0": [{"yanked": True}],
        }
    }
    with patch.object(
        updates.urllib.request, "urlopen", return_value=io.BytesIO(json.dumps(package).encode())
    ) as request:
        updates._fetch_latest(path)
    request.assert_called_once_with(updates.PYPI_URL, timeout=5)
    assert updates.read_state(path)["latestKnown"] == "0.7.0"


def test_compatibility_gate_accepts_same_major():
    major = int(package_version().split(".")[0])
    for suffix in (".0.0", ".99.0", ".1.0-rc.1", ".1.0-rc.1+sha", ".1.0+sha"):
        check_addin_compatibility(
            "ping", {"addinVersion": f"{major}{suffix}", "commands": ["ping"]}
        )


@pytest.mark.parametrize("reported", [None, "", "unknown", 2, "other-major"])
def test_compatibility_gate_rejects_missing_invalid_or_different_major(reported):
    major = int(package_version().split(".")[0])
    if reported == "other-major":
        reported = f"{major - 1 if major > 0 else major + 1}.1.0"
    instance = {"commands": ["ping"]}
    if reported is not None:
        instance["addinVersion"] = reported
    with pytest.raises(
        RevitChannelError, match=f"needs a Revit Model MCP add-in of major version {major}"
    ) as error:
        check_addin_compatibility("ping", instance)
    assert "Install the matching add-in from the releases page" in str(error.value)
    assert (reported if isinstance(reported, str) and reported else "no add-in version") in str(
        error.value
    )


def test_compatibility_gate_rejects_older_major():
    major = max(1, int(package_version().split(".")[0]))
    with patch("revit_model_mcp.package_version", return_value=f"{major}.0.0"):
        with pytest.raises(RevitChannelError, match=f"major version {major}"):
            check_addin_compatibility(
                "ping", {"addinVersion": f"{major - 1}.1.0", "commands": ["ping"]}
            )


@pytest.mark.parametrize("commands", [None, [], "ping", ["other-command"]])
def test_compatibility_gate_rejects_missing_command(commands):
    version = f"{package_version().split('.')[0]}.1.0"
    with pytest.raises(RevitChannelError, match=f"add-in {version} does not support ping"):
        check_addin_compatibility("ping", {"addinVersion": version, "commands": commands})


@pytest.mark.parametrize(
    ("cached", "current", "expected"),
    [
        ("0.7.0", "0.9.0", "0.9.0"),
        ("1.0.0", "0.9.0", "1.0.0"),
        (None, "0.9.0", "0.9.0"),
        (None, "0.0.0+unknown", None),
        ("0.7.0", "0.0.0+unknown", "0.7.0"),
    ],
)
def test_update_status_latest_known_is_never_older_than_current(cached, current, expected):
    state = {"latestKnown": cached} if cached else {}
    with patch.object(updates, "read_state", return_value=state):
        assert updates.update_status(current)["latestKnownVersion"] == expected
