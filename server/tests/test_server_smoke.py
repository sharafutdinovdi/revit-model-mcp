import json
import os
import subprocess
import sys
from pathlib import Path

import pytest

SMOKE = Path(__file__).resolve().parents[2] / "build/server-exe/smoke.py"


@pytest.mark.parametrize("deny_report", [False, True])
def test_source_server_audit_smoke(tmp_path, deny_report):
    temporary = tmp_path / "temporary"
    temporary.mkdir()
    if deny_report:
        policy = tmp_path / "RevitModelMcp" / "policy.json"
        policy.parent.mkdir()
        policy.write_text(json.dumps({"ToolDeny": ["revit_audit_report"]}), encoding="utf-8")

    result = subprocess.run(
        [
            sys.executable,
            str(SMOKE),
            f"--command={sys.executable}",
            "--command=-m",
            "--command=revit_model_mcp.server",
        ],
        env={
            **os.environ,
            "PROGRAMDATA": str(tmp_path),
            "TEMP": str(temporary),
            "TMP": str(temporary),
            "TMPDIR": str(temporary),
            "REVIT_MCP_TOOLSETS": "all",
        },
        capture_output=True,
        text=True,
        timeout=60,
    )

    assert "revit_model_audit: audited 1 snapshot and wrote audit JSON" in result.stdout
    assert not list(temporary.iterdir())
    if deny_report:
        assert result.returncode == 1
        assert "revit_audit_report failed" in result.stderr
        assert "is denied" in result.stderr
        assert "Smoke test passed" not in result.stdout
    else:
        assert result.returncode == 0, result.stderr
        assert "revit_audit_report: wrote HTML report" in result.stdout
        assert "Smoke test passed" in result.stdout
