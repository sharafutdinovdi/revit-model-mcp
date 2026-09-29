import io
import json
from datetime import datetime, timedelta, timezone
from unittest.mock import patch

import pytest

from revit_model_mcp import updates
from revit_model_mcp.revit_channel import (
    MIN_ADDIN_VERSION,
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


def test_compatibility_gate_uses_version_and_command_list():
    MIN_ADDIN_VERSION["future-command"] = "0.7.0"
    try:
        check_addin_compatibility("ping", {})
        check_addin_compatibility("ping", {"commands": []})
        check_addin_compatibility("ping", {"addinVersion": "0.6.0", "commands": ["ping"]})
        for instance in (
            {"addinVersion": "0.6.0", "commands": ["future-command"]},
            {},
        ):
            with pytest.raises(RevitChannelError, match="needs add-in 0.7.0 or later") as error:
                check_addin_compatibility("future-command", instance)
            assert "Install the latest add-in from the releases page" in str(error.value)
        with pytest.raises(RevitChannelError, match="workstation has 0.7.0"):
            check_addin_compatibility("ping", {"addinVersion": "0.7.0", "commands": []})
        with pytest.raises(RevitChannelError, match="workstation has 0.7.0"):
            check_addin_compatibility("ping", {"addinVersion": "0.7.0"})
    finally:
        del MIN_ADDIN_VERSION["future-command"]
