"""BCF XML 2.1 archive writer using only the standard library."""

import json
import uuid
import xml.etree.ElementTree as ET
from zipfile import ZIP_DEFLATED, ZipFile

from revit_model_mcp.audit.evidence import IFC_GUID, image_bytes


def _node(parent, name, value=None, **attributes):
    child = ET.SubElement(parent, name, attributes)
    if value is not None:
        child.text = str(value)
    return child


def _xml(node):
    return ET.tostring(node, encoding="utf-8", xml_declaration=True)


def write_bcf(result, stream):
    """Export failed and incomplete findings; unavailable evidence stays explicit."""
    if result.get("kind") == "batch_audit":
        raise ValueError("BCF export requires a single-model audit; export each model separately.")
    with ZipFile(stream, "w", compression=ZIP_DEFLATED) as archive:
        archive.writestr("bcf.version", _xml(ET.Element("Version", VersionId="2.1")))
        for finding in result["findings"]:
            if finding["status"] == "passed":
                continue
            topic_id, view_id = str(uuid.uuid4()), str(uuid.uuid4())
            markup = ET.Element("Markup")
            topic = _node(markup, "Topic", Guid=topic_id, TopicType="Issue", TopicStatus="Open")
            _node(topic, "Title", finding.get("title") or finding["id"])
            _node(topic, "Priority", finding["severity"])
            for label in (finding["id"], finding["severity"], finding["status"]):
                _node(topic, "Labels", label)
            _node(topic, "CreationDate", result["evaluatedAtUtc"])
            _node(topic, "CreationAuthor", "Model MCP")
            evidence = finding.get("evidence", {})
            description = {
                key: finding.get(key)
                for key in (
                    "status",
                    "measured",
                    "threshold",
                    "recommendation",
                    "reference",
                    "element_ids_truncated",
                    "skipped",
                    "skippedCount",
                )
            }
            description["evidence"] = {
                key: value
                for key, value in evidence.items()
                if key not in ("png", "camera", "components")
            }
            if not evidence.get("png"):
                description["snapshot"] = "Unavailable: no captured evidence image."
            if not evidence.get("camera"):
                description["camera"] = "Unavailable: viewpoint has component selection only."
            _node(topic, "Description", json.dumps(description, ensure_ascii=False))
            ids = finding.get("element_ids", [])
            image = image_bytes(finding)
            if ids or image:
                views = _node(markup, "Viewpoints", Guid=view_id)
                _node(views, "Viewpoint", "viewpoint.bcfv")
                if image:
                    _node(views, "Snapshot", "snapshot.png")
                    archive.writestr(f"{topic_id}/snapshot.png", image)
                visual = ET.Element("VisualizationInfo", Guid=view_id)
                if ids:
                    components = _node(visual, "Components")
                    selection = _node(components, "Selection")
                    metadata = {
                        str(row["authoringToolId"]): row for row in evidence.get("components", [])
                    }
                    for element_id in ids:
                        row = metadata.get(str(element_id), {})
                        guid = row.get("ifcGuid") or ""
                        component = _node(
                            selection,
                            "Component",
                            **({"IfcGuid": guid} if IFC_GUID.fullmatch(guid) else {}),
                        )
                        _node(
                            component,
                            "OriginatingSystem",
                            row.get("originatingSystem") or "Autodesk Revit",
                        )
                        _node(component, "AuthoringToolId", element_id)
                    _node(components, "Visibility", DefaultVisibility="true")
                if camera := evidence.get("camera"):
                    orthogonal = _node(visual, "OrthogonalCamera")
                    for name, key in (
                        ("CameraViewPoint", "position"),
                        ("CameraDirection", "direction"),
                        ("CameraUpVector", "up"),
                    ):
                        vector = _node(orthogonal, name)
                        for axis, coordinate in zip(("X", "Y", "Z"), camera[key], strict=True):
                            _node(vector, axis, coordinate)
                    _node(orthogonal, "ViewToWorldScale", camera["scale"])
                archive.writestr(f"{topic_id}/viewpoint.bcfv", _xml(visual))
            archive.writestr(f"{topic_id}/markup.bcf", _xml(markup))
