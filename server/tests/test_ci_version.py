import re
import tomllib
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
CI = ROOT / ".github" / "workflows" / "ci.yml"

pytestmark = pytest.mark.skipif(not CI.is_file(), reason="repository workflows are not available")


def _test_job() -> str:
    text = CI.read_text(encoding="utf-8")
    start = text.index("\n  test:\n")
    return text[start:]


def test_repository_version_is_plain_for_ci_suffix():
    pyproject = tomllib.loads((ROOT / "server" / "pyproject.toml").read_text(encoding="utf-8"))
    assert re.fullmatch(r"\d+\.\d+\.\d+", pyproject["project"]["version"])


def test_ci_resolves_version_from_the_repository():
    job = _test_job()
    assert "server/pyproject.toml" in job
    assert "CI_VERSION=" in job
    assert "0.0.0-ci" not in re.sub(r"New-WingetManifests\.ps1[^\n]*", "", job)


def test_every_add_in_build_in_ci_carries_the_repository_version():
    for line in _test_job().splitlines():
        if "dotnet build src/RevitModelMcp.Addin" in line or (
            "dotnet restore src/RevitModelMcp.Addin/RevitModelMcp.Addin.csproj" in line
        ):
            assert "-p:Version=$env:CI_VERSION" in line, line


def test_ci_installer_contains_every_supported_year():
    job = _test_job()
    assert "foreach ($year in '22', '23', '24', '25', '26', '27')" in job
    assert "$manifests.Count -ne 6" in job
    assert "-ne 3" not in job


def test_add_in_project_defaults_to_the_repository_version():
    project = (ROOT / "src" / "RevitModelMcp.Addin" / "RevitModelMcp.Addin.csproj").read_text(
        encoding="utf-8"
    )
    assert "server/pyproject.toml" in project
    assert "<VersionPrefix" in project
