import json
from unittest.mock import patch

import pytest

from revit_model_mcp import package_version
from revit_model_mcp.revit_channel import (
    RevitChannelError,
    check_addin_compatibility,
)


def test_compatibility_gate_accepts_same_major():
    major = int(package_version().split(".")[0])
    current = package_version().split("+")[0]
    for version in (f"{current}-dev", f"{current}-dev+2e56c3f8a1b2c3d4e5f6"):
        check_addin_compatibility("ping", {"addinVersion": version, "commands": ["ping"]})
    for suffix in (".0.0", ".99.0", ".1.0-rc.1", ".1.0-rc.1+sha", ".1.0+sha", ".1.0-dev+2e56c3f"):
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
        RevitChannelError, match=f"needs a Model MCP add-in of major version {major}"
    ) as error:
        check_addin_compatibility("ping", instance)
    assert "Update the Model MCP add-in from the Autodesk App Store." in str(error.value)
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


@pytest.mark.parametrize("instance", [{}, {"addinVersion": package_version(), "commands": []}])
def test_store_compatibility_guidance_has_no_url(instance):
    import subprocess
    import sys

    script = """
import json
import sys
import revit_model_mcp
revit_model_mcp.CHANNEL = "store"
from revit_model_mcp.revit_channel import check_addin_compatibility, RevitChannelError
try:
    check_addin_compatibility("ping", json.loads(sys.argv[1]))
except RevitChannelError as error:
    print(error)
else:
    raise AssertionError("Expected an incompatible add-in to be refused")
"""
    result = subprocess.run(
        [sys.executable, "-c", script, json.dumps(instance)],
        check=True,
        capture_output=True,
        text=True,
        timeout=30,
    )
    assert "Update the Model MCP add-in from the Autodesk App Store." in result.stdout
    assert "https://" not in result.stdout
