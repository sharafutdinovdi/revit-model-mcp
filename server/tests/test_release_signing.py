import re
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
CONFIG = ROOT / ".signpath" / "artifact-configuration.xml"
RELEASE = ROOT / ".github" / "workflows" / "release.yml"
NS = {"s": "http://signpath.io/artifact-configuration/v1"}

pytestmark = pytest.mark.skipif(not CONFIG.is_file(), reason="repository files are not available")


def _paths() -> list[str]:
    root = ET.parse(CONFIG).getroot()
    return [element.get("path") or "" for element in root.iter() if element.get("path")]


def test_artifact_configuration_never_lists_revit_api_assemblies():
    for path in _paths():
        assert not re.search(r"(AdWindows|UIFramework|RevitAPI|RevitNET)", path, re.IGNORECASE), (
            path
        )


def test_artifact_configuration_signs_only_own_binaries():
    pe_paths = {path.rsplit("/", 1)[-1] for path in _paths() if "/" in path}
    assert pe_paths == {
        "RevitModelMcp.dll",
        "RevitModelMcp.Core.dll",
        "RevitModelMcp.Updater.exe",
        "RevitModelMcp.BatchSupervisor.exe",
    }


def test_artifact_configuration_enforces_product_metadata_and_signs_the_msi():
    root = ET.parse(CONFIG).getroot()
    sets = root.findall(".//s:pe-file-set", NS)
    assert sets
    for item in sets:
        assert item.get("product-name") == "Model MCP"
        assert item.get("product-version") == "${product-version}"
        assert item.find("s:for-each/s:authenticode-sign", NS) is not None
    msi = root.find(".//s:msi-file", NS)
    assert msi is not None and msi.find("s:authenticode-sign", NS) is not None


def test_release_workflow_skips_signing_without_configuration():
    text = RELEASE.read_text(encoding="utf-8")
    assert "steps.signpath.outputs.enabled == 'true'" in text
    assert "signpath/github-action-submit-signing-request@" in text
    for line in text.splitlines():
        if "uses: signpath/" in line:
            assert re.search(r"@[0-9a-f]{40}\b", line), line
    for match in re.finditer(r"^      - name: (.+)\n((?:        .*\n)+)", text, re.MULTILINE):
        name, body = match.groups()
        signing_step = re.search(r"SignPath|signed|signatures", name)
        if signing_step and name != "Check SignPath configuration":
            assert "steps.signpath.outputs.enabled == 'true'" in body, name
