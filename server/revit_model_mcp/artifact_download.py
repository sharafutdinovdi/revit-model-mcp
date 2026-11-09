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
    snapshot = json.loads(content)
    from revit_model_mcp.audit.result import model_identity, model_key

    identity = model_identity(snapshot)
    if identity.get("centralPath") or identity.get("path"):
        snapshot["historyKey"] = model_key(identity)
    snapshot = redact_model_paths(snapshot)
    content = json.dumps(snapshot, ensure_ascii=False).encode("utf-8")
    try:
        directory = Path(output_dir).expanduser().absolute()
        directory.mkdir(parents=True, exist_ok=True)
        target = directory.resolve() / name
        write_new_file(target, lambda output: output.write(content))
    except OSError as error:
        raise ValueError(f"Cannot save batch artifact: {error}") from error
    return str(target)


def complete_code_result(result: dict[str, object]) -> None:
    data = result.get("data")
    if not isinstance(data, dict):
        return
    completeness = data.get("resultCompleteness")
    if not isinstance(completeness, dict) or not completeness.get("truncated"):
        return
    result["partial"] = True
    skipped = result.setdefault("skipped", [])
    receipt = next((item for item in skipped if item.get("what") == "returnValue"), None)
    if receipt is None:
        receipt = {"what": "returnValue", "reason": "Inline result truncated."}
        skipped.append(receipt)
        result["skippedCount"] = result.get("skippedCount", 0) + 1
    receipt.update(completeness)
    encoded = data.pop("resultArtifact", None)
    try:
        if not isinstance(encoded, str):
            raise ValueError("Full result artifact is missing.")
        content = json.loads(base64.b64decode(encoded, validate=True))
        content = json.dumps(redact_model_paths(content), ensure_ascii=False).encode("utf-8")
        target = Path(tempfile.mkdtemp(prefix="revit-code-result-")) / "result.json"
        write_new_file(target, lambda output: output.write(content))
        data["resultArtifact"] = {"path": str(target), "format": "json", "complete": True}
    except (ValueError, OSError) as error:
        data["resultArtifact"] = {"complete": False, "error": str(error)}
        skipped.append({"what": "resultArtifact", "reason": str(error)})
        result["skippedCount"] += 1
