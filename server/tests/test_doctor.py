import asyncio
import json

import pytest

from revit_model_mcp import doctor


def inventory(**changes):
    return {
        "revitYears": [2026],
        "addins": [{"year": 2026, "missing": [], "version": "2.0.0+commit"}],
        "serverExists": True,
        "channelExists": True,
        "clients": [],
        **changes,
    }


def test_healthy_inventory():
    result = doctor.assess_inventory(inventory(), "2.0.0")
    assert result["ok"]
    assert result["installedYears"] == [2026]
    assert not result["problems"]


@pytest.mark.parametrize(
    ("changes", "remediation"),
    [
        ({"revitYears": []}, "Install Revit"),
        ({"addins": []}, "Run the installer again"),
        ({"serverExists": False}, "Run the SingleUser MSI"),
        (
            {"addins": [{"year": 2026, "missing": ["Core.dll"], "version": "1.0.0"}]},
            "Repair the MSI",
        ),
        (
            {"addins": [{"year": 2026, "missing": [], "version": "1.0.0"}]},
            "Install the same release",
        ),
        ({"clients": [{"id": "cursor", "state": "Unreadable"}]}, "Back up its config"),
    ],
)
def test_problems_have_remediation(changes, remediation):
    result = doctor.assess_inventory(inventory(**changes), "2.0.0")
    assert not result["ok"]
    assert any(remediation in problem for problem in result["problems"])


def test_channel_missing_requires_human_start():
    result = doctor.assess_inventory(inventory(channelExists=False), "2.0.0")
    assert result["humanSteps"] == ["Start Revit to create the Model MCP channel folder."]


def test_non_windows(monkeypatch):
    monkeypatch.setattr(doctor.sys, "platform", "darwin")
    result = asyncio.run(doctor.diagnose("2.0.0"))
    assert not result["ok"]
    assert "SSH transport" in result["problems"][0]


def test_live_channel_diagnostics_redact_models(monkeypatch):
    class Host:
        def __init__(self, **kwargs):
            assert kwargs == {"local": True}

        async def _run(self, script):
            return json.dumps(inventory())

        async def list_revit_instances(self):
            return [
                {
                    "processId": 123,
                    "pluginResponding": False,
                    "channelLog": "File channel disabled: ACL refusal",
                    "documentPath": "private-model",
                }
            ]

    monkeypatch.setattr(doctor.sys, "platform", "win32")
    monkeypatch.setattr(doctor, "SshPowerShellHost", Host)
    monkeypatch.setattr(doctor, "LocalPipeHost", lambda fallback: fallback)
    result = asyncio.run(doctor.diagnose("2.0.0"))
    assert not result["ok"]
    assert "ACL refusal" in result["problems"][0]
    assert "Repair channel permissions" in result["problems"][0]
    assert "private-model" not in json.dumps(result)


def test_diagnostic_failure_is_json_result(monkeypatch):
    class Host:
        def __init__(self, **kwargs):
            pass

        async def _run(self, script):
            raise OSError("PowerShell unavailable")

    monkeypatch.setattr(doctor.sys, "platform", "win32")
    monkeypatch.setattr(doctor, "SshPowerShellHost", Host)
    monkeypatch.setattr(doctor, "LocalPipeHost", lambda fallback: fallback)
    result = asyncio.run(doctor.diagnose("2.0.0"))
    assert not result["ok"]
    assert "PowerShell unavailable" in result["problems"][0]


def test_uvx_does_not_require_a_bundled_executable():
    result = doctor.assess_inventory(
        inventory(serverExists=False), "2.0.0", require_bundled_server=False
    )
    assert result["ok"]


def test_store_desktop_remediation_uses_supported_client_name():
    result = doctor.assess_inventory(
        inventory(clients=[{"id": "claude-desktop-store", "state": "ConfiguredDifferentCommand"}]),
        "2.0.0",
    )
    assert not result["ok"]
    assert result["problems"][0].endswith("-Client claude-desktop.")
