"""Write a CycloneDX 1.5 SBOM for the NuGet packages locked by the shipped projects."""

from __future__ import annotations

import argparse
import json
import re
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path

SHIPPED_LOCKS = (
    "src/RevitModelMcp.Addin/packages.Release.R*.lock.json",
    "src/RevitModelMcp.Core/packages.lock.json",
    "src/RevitModelMcp.Updater/packages.lock.json",
    "src/RevitModelMcp.BatchSupervisor/packages.lock.json",
)
YEAR_PATTERN = re.compile(r"packages\.Release\.R(\d+)\.lock\.json$")


def collect(root: Path) -> dict[tuple[str, str], set[str]]:
    """Map (package, version) to the Revit years whose lock file resolves it."""
    packages: dict[tuple[str, str], set[str]] = {}
    for pattern in SHIPPED_LOCKS:
        for lock in sorted(root.glob(pattern)):
            match = YEAR_PATTERN.search(lock.name)
            year = {f"20{match.group(1)}"} if match else set()
            data = json.loads(lock.read_text(encoding="utf-8-sig"))
            for framework in data.get("dependencies", {}).values():
                for name, entry in framework.items():
                    version = entry.get("resolved")
                    if entry.get("type") == "Project" or not version:
                        continue
                    packages.setdefault((name, version), set()).update(year)
    return packages


def build(root: Path, product_version: str, now: datetime | None = None) -> dict:
    stamp = (now or datetime.now(timezone.utc)).strftime("%Y-%m-%dT%H:%M:%SZ")
    components = []
    for (name, version), years in sorted(
        collect(root).items(), key=lambda item: item[0]
    ):
        component = {
            "type": "library",
            "bom-ref": f"pkg:nuget/{name}@{version}",
            "name": name,
            "version": version,
            "purl": f"pkg:nuget/{name}@{version}",
        }
        if years:
            component["properties"] = [
                {
                    "name": "revit-model-mcp:revit-years",
                    "value": ",".join(sorted(years)),
                }
            ]
        components.append(component)
    return {
        "bomFormat": "CycloneDX",
        "specVersion": "1.5",
        "serialNumber": f"urn:uuid:{uuid.uuid4()}",
        "version": 1,
        "metadata": {
            "timestamp": stamp,
            "component": {
                "type": "application",
                "name": "revit-model-mcp-addin",
                "version": product_version,
            },
        },
        "components": components,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path("."))
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    bom = build(args.root, args.version)
    if not bom["components"]:
        print("No locked packages found.", file=sys.stderr)
        return 1
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(bom, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {len(bom['components'])} components to {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
