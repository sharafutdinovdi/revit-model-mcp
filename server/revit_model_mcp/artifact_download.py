from __future__ import annotations

import base64
import tempfile
from pathlib import Path


def save_artifact(result: dict[str, object], save_to: str | None) -> str:
    name = result.get("artifactName")
    encoded = result.get("artifact")
    if not isinstance(name, str) or Path(name).name != name or not isinstance(encoded, str):
        raise ValueError("Remote response does not contain a safe image artifact.")
    target = (
        Path(save_to).expanduser().resolve()
        if save_to
        else Path(tempfile.mkdtemp(prefix="revit-view-")) / name
    )
    if target.exists():
        raise ValueError(f"Local file already exists: {target}")
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(base64.b64decode(encoded, validate=True))
    return str(target)
