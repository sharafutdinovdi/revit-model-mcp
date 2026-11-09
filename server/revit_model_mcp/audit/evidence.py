"""Bounded capture of audit evidence through the existing snapshot path."""

import asyncio
import base64
import re
import tempfile
from pathlib import Path

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
IFC_GUID = re.compile(r"[0-3][0-9A-Za-z_$]{21}\Z")


def image_bytes(finding):
    encoded = finding.get("evidence", {}).get("png")
    if not encoded:
        return None
    payload = base64.b64decode(encoded, validate=True)
    if not payload.startswith(PNG_SIGNATURE):
        raise ValueError("Evidence image must be PNG.")
    return payload


async def capture_findings(findings, capture, *, max_images=25, budget_seconds=210):
    """Record every requested capture outcome without changing rule assessments."""
    deadline = asyncio.get_running_loop().time() + budget_seconds
    candidates = [f for f in findings if f.get("element_ids") and f["status"] != "passed"]
    severity = {"critical": 0, "major": 1, "minor": 2, "info": 3}
    candidates.sort(key=lambda f: severity.get(f["severity"], 4))
    with tempfile.TemporaryDirectory(prefix="revit_audit_evidence_") as directory:
        for index, finding in enumerate(candidates):
            evidence = {"status": "unavailable", "skipped": [], "skippedCount": 0}
            finding["evidence"] = evidence
            remaining = deadline - asyncio.get_running_loop().time()
            reason = None
            if index >= max_images:
                reason = "Evidence image count budget exhausted."
            elif remaining <= 0:
                reason = "Evidence capture time budget exhausted."
            else:
                try:
                    async with asyncio.timeout(remaining):
                        response = await capture(
                            finding["element_ids"][:500],
                            str(Path(directory) / f"{index}.png"),
                            min(120, max(1, int(remaining))),
                        )
                    data = response["data"]
                    payload = Path(data["localPath"]).read_bytes()
                    evidence["png"] = base64.b64encode(payload).decode("ascii")
                    image_bytes(finding)
                    evidence["components"] = data.get("components", [])
                    evidence["camera"] = data.get("camera")
                    omissions = list(response.get("skipped", []))
                    if response.get("skippedCount", 0) > len(omissions):
                        omissions.append("Capture reported additional skipped data.")
                    if data.get("missingIds"):
                        omissions.append({"missingIds": data["missingIds"]})
                    if len(finding["element_ids"]) > 500:
                        omissions.append("Capture limited to 500 element IDs.")
                    evidence["status"] = "partial" if omissions else "captured"
                    evidence["skipped"] = omissions
                    evidence["skippedCount"] = max(len(omissions), response.get("skippedCount", 0))
                except Exception as error:
                    # Capture failures are evidence, not a reason to discard assessed findings.
                    evidence.pop("png", None)
                    reason = str(error) or "Evidence capture time budget exhausted."
            if reason:
                evidence["skipped"] = [reason]
                evidence["skippedCount"] = 1
            finding.setdefault("skipped", []).extend(
                {"evidence": entry} for entry in evidence["skipped"]
            )
            finding["skippedCount"] = finding.get("skippedCount", 0) + evidence["skippedCount"]
