from __future__ import annotations

import json
import runpy
from datetime import datetime, timezone
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
sbom = runpy.run_path(str(REPOSITORY_ROOT / "build" / "sbom_dotnet.py"))


def write_lock(path: Path, packages: dict[str, tuple[str, str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    entries = {
        name: {"type": kind, **({"resolved": version} if version else {})}
        for name, (kind, version) in packages.items()
    }
    path.write_text(json.dumps({"version": 2, "dependencies": {"net8.0": entries}}))


def test_components_are_deduplicated_with_revit_years(tmp_path):
    addin = tmp_path / "src/RevitModelMcp.Addin"
    write_lock(
        addin / "packages.Release.R25.lock.json",
        {"Polyfill": ("Direct", "11.0.1"), "revitmodelmcp.core": ("Project", None)},
    )
    write_lock(addin / "packages.Release.R26.lock.json", {"Polyfill": ("Direct", "11.0.1")})
    write_lock(
        tmp_path / "src/RevitModelMcp.Core/packages.lock.json",
        {"Polyfill": ("Direct", "11.0.1"), "Other": ("Transitive", "1.0.0")},
    )

    bom = sbom["build"](tmp_path, "1.2.3", datetime(2026, 1, 2, 3, 4, 5, tzinfo=timezone.utc))

    assert bom["bomFormat"] == "CycloneDX"
    assert bom["specVersion"] == "1.5"
    assert bom["metadata"]["timestamp"] == "2026-01-02T03:04:05Z"
    assert bom["metadata"]["component"]["version"] == "1.2.3"
    names = [(c["name"], c["version"]) for c in bom["components"]]
    assert names == [("Other", "1.0.0"), ("Polyfill", "11.0.1")]
    polyfill = bom["components"][1]
    assert polyfill["purl"] == "pkg:nuget/Polyfill@11.0.1"
    assert polyfill["properties"][0]["value"] == "2025,2026"


def test_main_fails_without_locks(tmp_path, capsys):
    code = sbom["main"](
        ["--root", str(tmp_path), "--version", "1", "--output", str(tmp_path / "o.json")]
    )
    assert code == 1


def test_real_repository_locks_produce_roslyn_component(tmp_path):
    output = tmp_path / "sbom.json"
    assert (
        sbom["main"](["--root", str(REPOSITORY_ROOT), "--version", "0", "--output", str(output)])
        == 0
    )
    components = json.loads(output.read_text())["components"]
    assert any(c["name"] == "Microsoft.CodeAnalysis.CSharp" for c in components)
