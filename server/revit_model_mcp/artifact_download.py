from __future__ import annotations

import base64
import json
import os
import tempfile
from pathlib import Path

from revit_model_mcp.actions import redact_model_paths
from revit_model_mcp.atomic_write import write_new_file


def preflight_artifact_target(output_path: str | None) -> None:
    if not output_path:
        return
    if Path(output_path).suffix.lower() != ".png":
        raise ValueError("The image destination must have a .png extension.")
    target = Path(output_path).expanduser().absolute()
    if target.exists() or target.is_symlink():
        raise ValueError(f"Local file already exists: {target}")
    try:
        target.parent.mkdir(parents=True, exist_ok=True)
        descriptor, temporary = tempfile.mkstemp(prefix=f".{target.name}.", dir=target.parent)
        try:
            os.close(descriptor)
        finally:
            os.unlink(temporary)
    except OSError as error:
        raise ValueError(f"Cannot save image: {error}") from error


def save_artifact(result: dict[str, object], output_path: str | None) -> str:
    name = result.get("artifactName")
    encoded = result.get("artifact")
    if not isinstance(name, str) or Path(name).name != name or not isinstance(encoded, str):
        raise ValueError("Remote response does not contain a safe image artifact.")
    if output_path and Path(output_path).suffix.lower() != ".png":
        raise ValueError("The image destination must have a .png extension.")
    image = base64.b64decode(encoded, validate=True)
    if not image.startswith(b"\x89PNG\r\n\x1a\n"):
        raise ValueError("The Revit endpoint did not return a PNG image.")
    try:
        target = (
            Path(output_path).expanduser().absolute()
            if output_path
            else Path(tempfile.mkdtemp(prefix="revit-view-")) / name
        )
        target.parent.mkdir(parents=True, exist_ok=True)
        target = target.parent.resolve() / target.name
        write_new_file(target, lambda output: output.write(image))
    except OSError as error:
        raise ValueError(f"Cannot save image: {error}") from error
    return str(target)


def save_batch_artifact(result: dict[str, object], output_dir: str) -> str:
    name = result.get("artifactName")
    encoded = result.get("artifact")
    if (
        not isinstance(name, str)
        or not name.startswith("snapshot_")
        or not name.endswith(".json")
        or Path(name).name != name
        or not isinstance(encoded, str)
    ):
        raise ValueError("Remote response does not contain a safe batch artifact.")
    content = base64.b64decode(encoded, validate=True)
    snapshot = redact_model_paths(json.loads(content))
    content = json.dumps(snapshot, ensure_ascii=False).encode("utf-8")
    try:
        directory = Path(output_dir).expanduser().absolute()
        directory.mkdir(parents=True, exist_ok=True)
        target = directory.resolve() / name
        write_new_file(target, lambda output: output.write(content))
    except OSError as error:
        raise ValueError(f"Cannot save batch artifact: {error}") from error
    return str(target)
