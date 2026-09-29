from __future__ import annotations

import base64
import tempfile
from pathlib import Path


def save_artifact(result: dict[str, object], save_to: str | None) -> str:
    name = result.get("artifactName")
    encoded = result.get("artifact")
    if not isinstance(name, str) or Path(name).name != name or not isinstance(encoded, str):
        raise ValueError("Remote response does not contain a safe image artifact.")
    if save_to and Path(save_to).suffix.lower() != ".png":
        raise ValueError("The image destination must have a .png extension.")
    image = base64.b64decode(encoded, validate=True)
    if not image.startswith(b"\x89PNG\r\n\x1a\n"):
        raise ValueError("The Revit endpoint did not return a PNG image.")
    target = (
        Path(save_to).expanduser().absolute()
        if save_to
        else Path(tempfile.mkdtemp(prefix="revit-view-")) / name
    )
    target.parent.mkdir(parents=True, exist_ok=True)
    target = target.parent.resolve() / target.name
    try:
        with target.open("xb") as output:
            output.write(image)
    except FileExistsError as error:
        raise ValueError(f"Local file already exists: {target}") from error
    return str(target)
