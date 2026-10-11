from __future__ import annotations

import runpy
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
build = runpy.run_path(str(REPOSITORY_ROOT / "build" / "server-exe" / "build.py"))


def test_distribution_name_strips_dist_info_and_version():
    name = build["distribution_name"]
    assert name("altgraph-0.17.5.dist-info") == "altgraph"
    assert name("pyinstaller_hooks_contrib-2026.1.dist-info") == "pyinstaller-hooks-contrib"
    assert name("typing_extensions-4.16.0.dist-info") == "typing-extensions"
    assert name("Pillow-12.3.0.dist-info") == "pillow"
    assert name("rpds_py-2026.6.3.dist-info") == "rpds-py"


def test_build_only_covers_build_tools_but_not_runtime_packages():
    build_only = build["BUILD_ONLY"]
    for package in (
        "pyinstaller",
        "pyinstaller-hooks-contrib",
        "altgraph",
        "pefile",
        "hatchling",
        "pip",
        "setuptools",
    ):
        assert package in build_only
    for package in ("mcp", "pydantic", "typing-extensions"):
        assert package not in build_only


def test_collect_licenses_names_folders_by_package_and_skips_build_tools(tmp_path):
    site = tmp_path / "venv" / "Lib" / "site-packages"
    for dist in ("mcp-2.3.0.dist-info", "pyinstaller-6.20.0.dist-info"):
        licenses = site / dist / "licenses"
        licenses.mkdir(parents=True)
        (licenses / "LICENSE").write_text("text")
    destination = tmp_path / "out"
    copied = build["collect_licenses"](tmp_path / "venv", destination)
    assert copied == 1
    assert (destination / "mcp" / "LICENSE").is_file()
    assert not (destination / "pyinstaller").exists()
