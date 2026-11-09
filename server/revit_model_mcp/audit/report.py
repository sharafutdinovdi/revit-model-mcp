"""Self-contained local audit reports with literal workbook cells."""

import html
import json
from pathlib import Path

from openpyxl import Workbook

from revit_model_mcp.atomic_write import write_new_file
from revit_model_mcp.audit.bcf import write_bcf
from revit_model_mcp.audit.evidence import image_bytes
from revit_model_mcp.audit.result import redact_path
from revit_model_mcp.workbook_style import _append, _header

HEADERS = [
    "Rule",
    "Severity",
    "Status",
    "Measured",
    "Threshold",
    "Population",
    "Element IDs",
    "Recommendation",
    "Reference",
    "Evidence",
]
FIELDS = [
    "id",
    "severity",
    "status",
    "measured",
    "threshold",
    "population",
    "element_ids",
    "recommendation",
    "reference",
    "evidence",
]


def text(value):
    return (
        json.dumps(value, ensure_ascii=False)
        if isinstance(value, (dict, list))
        else str(value)
        if value is not None
        else ""
    )


def tables(result):
    if result.get("kind") == "batch_audit":
        from revit_model_mcp.audit.batch import report_tables

        return report_tables(result, tables)
    content = {
        "Summary": [
            ["Field", "Value"],
            ["Model", result["model"]],
            ["Evaluated at UTC", result["evaluatedAtUtc"]],
            ["Rules", result["rules"]],
            ["Summary", result["summary"]],
        ],
        "Findings": [
            HEADERS,
            *[
                [text(f.get(k)) if k != "evidence" else "" for k in FIELDS]
                for f in result["findings"]
            ],
        ],
    }
    if "deltas" in result:
        content["Deltas"] = [
            ["Rule", "Element ID", "Status", "Severity"],
            *[
                [d[k] for k in ("rule", "element_id", "status", "severity")]
                for d in result["deltas"]
            ],
        ]
    content["Rules"] = [
        ["Rule", "Definition"],
        *[[r["id"], text(r)] for r in result.get("ruleDefinitions", result["findings"])],
    ]
    content["Skipped"] = [
        ["Entry"],
        *[[text(s)] for s in result["skipped"]],
        ["Skipped count: " + str(result["skippedCount"])],
    ]
    return content


def build_report(audit, output, format="both"):
    if format not in ("xlsx", "html", "both", "bcf", "all"):
        raise ValueError("format must be xlsx, html, both, bcf or all.")
    result = json.loads(Path(audit).expanduser().read_text(encoding="utf-8"))
    if result.get("schema") != 1 or not (
        isinstance(result.get("findings"), list)
        or (result.get("kind") == "batch_audit" and isinstance(result.get("models"), list))
    ):
        raise ValueError("Expected a schema 1 audit result.")
    base = Path(output).expanduser().absolute()
    formats = (
        ["xlsx", "html", "bcf"]
        if format == "all"
        else ["xlsx", "html"]
        if format == "both"
        else [format]
    )
    targets = [
        base.with_suffix(".bcfzip" if extension == "bcf" else "." + extension)
        for extension in formats
    ]
    images = {
        index: image
        for index, finding in enumerate(result["findings"])
        if (image := image_bytes(finding)) is not None
    }
    image_targets = (
        {index: base.with_name(base.name + f".evidence-{index + 1}.png") for index in images}
        if "xlsx" in formats
        else {}
    )
    for target in [*targets, *image_targets.values()]:
        if target.exists() or target.is_symlink():
            raise ValueError(f"Local file already exists: {target}")
        if not target.parent.is_dir():
            raise ValueError("Output parent directory must exist.")
    content = tables(result)
    for index, finding in enumerate(result["findings"]):
        evidence = finding.get("evidence", {})
        content["Findings"][index + 1][-1] = text(
            {
                key: value
                for key, value in evidence.items()
                if key not in ("png", "components", "camera")
            }
        )
    for index, target in image_targets.items():
        write_new_file(target, lambda stream, index=index: stream.write(images[index]))
    for extension, target in zip(formats, targets):
        if extension == "xlsx":
            workbook = Workbook()
            workbook.remove(workbook.active)
            for title, rows in content.items():
                sheet = workbook.create_sheet(title)
                for row in rows:
                    _append(sheet, [text(v) for v in row])
                _header(sheet, [26] * len(rows[0]), filter_rows=True)
                if title == "Findings":
                    for index, image_target in image_targets.items():
                        cell = sheet.cell(index + 2, len(HEADERS))
                        cell.hyperlink = image_target.name
                        cell.style = "Hyperlink"
            write_new_file(target, workbook.save)
        elif extension == "bcf":
            write_new_file(target, lambda stream: write_bcf(result, stream))
        else:

            def table(rows):
                return (
                    "<table>"
                    + "".join(
                        "<tr>"
                        + "".join("<td>" + html.escape(text(v)) + "</td>" for v in row)
                        + "</tr>"
                        for row in rows
                    )
                    + "</table>"
                )

            sections = []
            for title, rows in content.items():
                sections.append("<h2>" + title + "</h2>")
                if title == "Findings":
                    severity_column = rows[0].index("Severity")
                    for severity in ("critical", "major", "minor", "info"):
                        sections.append("<h3>" + severity + "</h3>")
                        sections.append(
                            table(
                                [
                                    rows[0],
                                    *[row for row in rows[1:] if row[severity_column] == severity],
                                ]
                            )
                        )
                    for index, finding in enumerate(result["findings"]):
                        if index in images:
                            sections.append(
                                "<figure><figcaption>"
                                + html.escape(str(finding["id"]))
                                + '</figcaption><img alt="Finding evidence" style="max-width:100%" src="data:image/png;base64,'
                                + finding["evidence"]["png"]
                                + '"></figure>'
                            )
                else:
                    sections.append(table(rows))
            document = (
                '<!doctype html><html lang="en"><meta charset="utf-8"><title>Model audit</title><style>body{font-family:system-ui;margin:2rem}table{border-collapse:collapse;width:100%}td{border:1px solid #bbb;padding:.5rem;overflow-wrap:anywhere}tr:first-child{font-weight:bold}</style><h1>Model audit</h1>'
                + "".join(sections)
                + "</html>"
            )
            write_new_file(target, lambda stream: stream.write(document.encode("utf-8")))
    return {
        "files": [redact_path(path) for path in [*targets, *image_targets.values()]],
        "skipped": result.get("skipped", []),
        "skippedCount": result.get("skippedCount", 0),
    }
