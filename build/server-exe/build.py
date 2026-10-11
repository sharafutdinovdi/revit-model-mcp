"""Build the self-contained Windows server (PyInstaller onedir) into output/server-exe/revit-model-mcp.

Needs only Python 3.11+ with venv and pip on the build machine. Every dependency installs with
--require-hashes: the server's locked set (server/requirements.docker.txt) plus the pinned build tools
(build/server-exe/requirements-build.txt). The end user needs neither Python nor uv.
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
NAME = "revit-model-mcp"
LICENSE_NAMES = ("LICENSE", "LICENCE", "COPYING", "NOTICE", "AUTHORS")
# PyInstaller and its hooks are build tools; only their bootloader ships, covered by the PyInstaller license.
BUILD_ONLY = {
    "altgraph",
    "packaging",
    "pefile",
    "pyinstaller",
    "pyinstaller-hooks-contrib",
    "pywin32-ctypes",
    "setuptools",
    "pip",
    "hatchling",
    "pathspec",
    "pluggy",
    "trove-classifiers",
}


def run(*command: str | Path) -> None:
    print("+", *command, flush=True)
    subprocess.run([str(part) for part in command], check=True, cwd=ROOT)


def venv_python(venv: Path) -> Path:
    return venv / ("Scripts/python.exe" if sys.platform == "win32" else "bin/python")


def collect_licenses(venv: Path, destination: Path) -> int:
    """Copy license files of every bundled distribution; returns how many files were copied."""
    copied = 0
    for dist_info in sorted(venv.glob("**/site-packages/*.dist-info")):
        package = dist_info.name.rsplit("-", 1)[0].replace("_", "-").lower()
        if package in BUILD_ONLY:
            continue
        files = [
            path
            for path in dist_info.rglob("*")
            if path.is_file() and path.name.upper().startswith(LICENSE_NAMES)
        ]
        for path in files:
            target = destination / package / path.name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(path, target)
            copied += 1
    return copied


def copy_python_license(destination: Path) -> None:
    for candidate in (
        Path(sys.base_prefix) / "LICENSE.txt",
        Path(sys.base_prefix) / "LICENSE",
    ):
        if candidate.is_file():
            shutil.copyfile(candidate, destination / "PYTHON-LICENSE.txt")
            return
    raise SystemExit(f"Python license not found under {sys.base_prefix}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "output" / "server-exe")
    args = parser.parse_args()
    output: Path = args.output.resolve()
    venv = output / "venv"
    work = output / "work"
    final = output / NAME
    for folder in (venv, work, final):
        shutil.rmtree(folder, ignore_errors=True)
    output.mkdir(parents=True, exist_ok=True)

    run(sys.executable, "-m", "venv", venv)
    python = venv_python(venv)
    run(
        python,
        "-m",
        "pip",
        "install",
        "--require-hashes",
        "--no-deps",
        "--only-binary=:all:",
        "-r",
        HERE / "requirements-build.txt",
    )
    run(
        python,
        "-m",
        "pip",
        "install",
        "--require-hashes",
        "--no-deps",
        "--only-binary=:all:",
        "-r",
        ROOT / "server" / "requirements.docker.txt",
    )
    run(
        python,
        "-m",
        "pip",
        "install",
        "--no-deps",
        "--no-build-isolation",
        ROOT / "server",
    )
    run(
        python,
        "-m",
        "PyInstaller",
        "--noconfirm",
        "--clean",
        "--onedir",
        "--console",
        "--name",
        NAME,
        "--distpath",
        output,
        "--workpath",
        work,
        "--specpath",
        work,
        "--copy-metadata",
        NAME,
        "--collect-data",
        "revit_model_mcp",
        "--collect-submodules",
        "revit_model_mcp",
        "--collect-data",
        "mcp",
        "--collect-data",
        "jsonschema_specifications",
        "--collect-data",
        "jsonschema",
        HERE / "entry.py",
    )

    exe = final / f"{NAME}.exe" if sys.platform == "win32" else final / NAME
    if not exe.is_file():
        raise SystemExit(f"Missing {exe}")
    notices = final / "licenses"
    notices.mkdir(exist_ok=True)
    count = collect_licenses(venv, notices)
    copy_python_license(notices)
    shutil.copyfile(ROOT / "LICENSE", final / "LICENSE")
    shutil.copyfile(ROOT / "THIRD-PARTY-NOTICES.md", final / "THIRD-PARTY-NOTICES.md")
    print(f"Built {exe} with {count} bundled license files", flush=True)


if __name__ == "__main__":
    main()
