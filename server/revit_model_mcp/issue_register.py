"""Validate review findings and write a local issue workbook."""

from collections import Counter
from datetime import date
from io import BytesIO
from pathlib import Path
from typing import Any

from openpyxl import Workbook
from openpyxl.cell.cell import ILLEGAL_CHARACTERS_RE
from openpyxl.chart import BarChart, Reference
from openpyxl.drawing.image import Image
from openpyxl.drawing.spreadsheet_drawing import AnchorMarker, OneCellAnchor
from openpyxl.drawing.xdr import XDRPositiveSize2D
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter
from openpyxl.utils.units import pixels_to_EMU
from PIL import Image as PillowImage
from PIL import ImageChops, ImageOps

SEVERITIES = ("critical", "major", "minor", "info")
COLORS = ("C00000", "FF8C00", "FFD966", "D9E1F2")
SNAPSHOT_LIMIT = 25
ACCENT = "1F3864"
BORDER = Border(*(Side(style="thin", color="BFBFBF") for _ in range(4)))
HEADERS = [
    "ID",
    "Snapshot",
    "Title",
    "Severity",
    "Category",
    "Requirement source",
    "Requirement",
    "Finding",
    "Recommendation",
    "Status",
    "Responsible",
    "Due",
    "Elements",
    "Element IDs",
]


def validate_capture(element_ids, pixel_size=1600, padding_mm=1500, mode="3d"):
    if not isinstance(element_ids, list) or not 1 <= len(element_ids) <= 500:
        raise ValueError("element_ids must contain 1 to 500 IDs.")
    if any(type(value) is not int or value <= 0 for value in element_ids):
        raise ValueError("element_ids must contain positive integers.")
    if type(pixel_size) is not int or not 1 <= pixel_size <= 4000:
        raise ValueError("pixel_size must be an integer from 1 to 4000.")
    if type(padding_mm) not in (int, float) or not 0 <= padding_mm <= 20000:
        raise ValueError("padding_mm must be from 0 to 20000.")
    if mode not in ("3d", "plan"):
        raise ValueError("mode must be 3d or plan.")


def validate_register(output_path, project, issues, pixel_size=900):
    """Return a destination and normalized input before any Revit request."""
    if not isinstance(output_path, str) or Path(output_path).suffix.lower() != ".xlsx":
        raise ValueError("output_path must have a .xlsx extension.")
    target = Path(output_path).expanduser().absolute()
    if target.exists() or target.is_symlink():
        raise ValueError(f"output_path already exists: {target}")
    if not isinstance(project, dict):
        raise ValueError("project must be an object.")
    for field in ("name", "model", "reviewer", "client", "stage", "date"):
        if field in project and not isinstance(project[field], str):
            raise ValueError(f"project.{field} must be a string.")
    documents = project.get("documents", [])
    if not isinstance(documents, list):
        raise ValueError("project.documents must be a list.")
    for index, document in enumerate(documents):
        if not isinstance(document, dict):
            raise ValueError(f"project.documents[{index}] must be an object.")
        for field in ("title", "reference", "revision"):
            if field in document and not isinstance(document[field], str):
                raise ValueError(f"project.documents[{index}].{field} must be a string.")
    if type(pixel_size) is not int or not 1 <= pixel_size <= 4000:
        raise ValueError("pixel_size must be an integer from 1 to 4000.")
    if not isinstance(issues, list) or not 1 <= len(issues) <= 60:
        raise ValueError("issues must contain 1 to 60 objects.")

    def check_text(value, field):
        if isinstance(value, str) and (ILLEGAL_CHARACTERS_RE.search(value) or len(value) > 32767):
            raise ValueError(
                f"{field} contains unsupported Excel text or exceeds 32767 characters."
            )

    for field, value in project.items():
        check_text(value, f"project.{field}")
    for index, document in enumerate(documents):
        for field, value in document.items():
            check_text(value, f"project.documents[{index}].{field}")
    normalized = []
    identifiers = set()
    for index, issue in enumerate(issues):
        prefix = f"issues[{index}]"
        if not isinstance(issue, dict):
            raise ValueError(f"{prefix} must be an object.")
        item = dict(issue)
        for field, value in item.items():
            check_text(value, f"{prefix}.{field}")
        for field in ("title", "category", "finding", "severity"):
            if not isinstance(item.get(field), str) or not item[field].strip():
                raise ValueError(f"{prefix}.{field} is required and must be a non-blank string.")
        for field in (
            "id",
            "requirement_source",
            "requirement",
            "recommendation",
            "status",
            "responsible",
            "due",
        ):
            if field in item and not isinstance(item[field], str):
                raise ValueError(f"{prefix}.{field} must be a string.")
        if item["severity"] not in SEVERITIES:
            raise ValueError(f"{prefix}.severity must be critical, major, minor or info.")
        item.setdefault("id", f"ISS-{index + 1:03d}")
        if not item["id"].strip() or item["id"] in identifiers:
            raise ValueError(f"{prefix}.id must be non-blank and unique.")
        identifiers.add(item["id"])
        item.setdefault("status", "Open")
        ids = item.get("element_ids", [])
        if (
            not isinstance(ids, list)
            or len(ids) > 500
            or any(type(value) is not int or value <= 0 for value in ids)
        ):
            raise ValueError(f"{prefix}.element_ids must contain at most 500 positive integers.")
        item["element_ids"] = list(dict.fromkeys(ids))
        item.setdefault("snapshot", "3d" if ids else "none")
        if item["snapshot"] not in ("3d", "plan", "none"):
            raise ValueError(f"{prefix}.snapshot must be 3d, plan or none.")
        check_text(f"{item['title']}\n{item['finding']}", f"{prefix}.finding")
        normalized.append(item)
    return target, dict(project), normalized


def snapshot_indices(issues):
    """Prioritize captures by severity, preserving input order within each severity."""
    candidates = [
        index
        for index, issue in enumerate(issues)
        if issue["element_ids"] and issue["snapshot"] != "none"
    ]
    ordered = sorted(candidates, key=lambda index: SEVERITIES.index(issues[index]["severity"]))
    return ordered[:SNAPSHOT_LIMIT], ordered[SNAPSHOT_LIMIT:]


def _append(sheet, values):
    sheet.append(values)
    if not values:
        return
    for cell in sheet[sheet.max_row]:
        if isinstance(cell.value, str):
            cell.data_type = "s"
        cell.font = Font(name="Calibri", size=10)
        cell.alignment = Alignment(wrap_text=True, vertical="top")
        cell.border = BORDER
        if sheet.max_row % 2 == 0:
            cell.fill = PatternFill("solid", fgColor="F2F2F2")


def _header(sheet, widths=None, row=1, columns=None, filter_rows=False):
    for cell in sheet[row][: columns or sheet.max_column]:
        cell.fill = PatternFill("solid", fgColor=ACCENT)
        cell.font = Font(name="Calibri", size=10, bold=True, color="FFFFFF")
        cell.alignment = Alignment(wrap_text=True, vertical="center")
        cell.border = BORDER
    sheet.row_dimensions[row].height = 30
    for index, width in enumerate(widths or [], 1):
        sheet.column_dimensions[get_column_letter(index)].width = width
    if filter_rows:
        sheet.freeze_panes = "A2"
        sheet.auto_filter.ref = sheet.dimensions


def _cover_block(sheet, row, first, last, value, color=None, size=10, bold=False, end_row=None):
    sheet.merge_cells(start_row=row, start_column=first, end_row=end_row or row, end_column=last)
    cell = sheet.cell(row, first, value)
    if isinstance(value, str):
        cell.data_type = "s"
    cell.font = Font(
        name="Calibri",
        size=size,
        bold=bold,
        color="FFFFFF" if color in (ACCENT, COLORS[0]) else "404040",
    )
    cell.alignment = Alignment(
        wrap_text=True, vertical="center", horizontal="center" if color else "left"
    )
    if color:
        for column in range(first, last + 1):
            sheet.cell(row, column).fill = PatternFill("solid", fgColor=color)


def _snapshot(path):
    with PillowImage.open(path) as source:
        image = source.convert("RGBA")
        white = PillowImage.new("RGBA", image.size, "white")
        image = PillowImage.alpha_composite(white, image).convert("RGB")
    difference = ImageChops.difference(image, PillowImage.new("RGB", image.size, "white"))
    bounds = difference.convert("L").point(lambda value: 255 if value > 10 else 0).getbbox()
    if bounds:
        left, top, right, bottom = bounds
        image = image.crop(
            (
                max(0, left - 12),
                max(0, top - 12),
                min(image.width, right + 12),
                min(image.height, bottom + 12),
            )
        )
    image = ImageOps.contain(image, (360, 240), PillowImage.Resampling.LANCZOS)
    canvas = PillowImage.new("RGB", (360, 240), "white")
    canvas.paste(image, ((360 - image.width) // 2, (240 - image.height) // 2))
    buffer = BytesIO()
    canvas.save(buffer, format="PNG")
    buffer.seek(0)
    return Image(buffer)


def write_register(
    output_path: str,
    project: dict[str, Any],
    issues: list[dict[str, Any]],
    snapshots: dict[int, str | Path] | None = None,
    snapshot_notes: dict[int, str] | None = None,
    warnings: list[str] | None = None,
) -> dict[str, Any]:
    """Write a new workbook. Snapshot paths remain readable until save completes."""
    target, project, issues = validate_register(output_path, project, issues)
    snapshots = snapshots or {}
    notes = dict(snapshot_notes or {})
    warnings = list(warnings or [])
    _, skipped = snapshot_indices(issues)
    for index in skipped:
        notes[index] = "Snapshot skipped: limit of 25 per register"
        warning = f"{issues[index]['id']}: {notes[index]}"
        if warning not in warnings:
            warnings.append(warning)
    for index, note in notes.items():
        warning = f"{issues[index]['id']}: {note}"
        if warning not in warnings:
            warnings.append(warning)
    workbook = Workbook()
    cover = workbook.active
    cover.title = "Cover"
    summary = workbook.create_sheet("Summary")
    register = workbook.create_sheet("Register")
    elements = workbook.create_sheet("Elements")
    workbook.properties.title = f"{project.get('name', '')} issue register".strip()
    workbook.properties.creator = project.get("reviewer", "")
    totals = {
        severity: sum(issue["severity"] == severity for issue in issues) for severity in SEVERITIES
    }
    for column in range(1, 9):
        cover.column_dimensions[get_column_letter(column)].width = 14
    _cover_block(cover, 1, 1, 8, "Model review: issue register", ACCENT, 22, True, end_row=2)
    for row in (1, 2):
        cover.row_dimensions[row].height = 28
        for column in range(1, 9):
            cover.cell(row, column).fill = PatternFill("solid", fgColor=ACCENT)
    _cover_block(
        cover, 3, 1, 8, f"{project.get('name', '')} | {project.get('model', '')}", ACCENT, 12
    )
    cover.row_dimensions[3].height = 26
    _cover_block(cover, 5, 1, 8, "Project", size=12, bold=True)
    for row, field in enumerate(("name", "model", "client", "stage", "reviewer", "date"), 6):
        _cover_block(cover, row, 1, 2, field.capitalize(), "F2F2F2", bold=True)
        _cover_block(
            cover,
            row,
            3,
            8,
            project.get(field, date.today().isoformat() if field == "date" else ""),
        )
        cover.row_dimensions[row].height = 24
    _cover_block(cover, 13, 1, 8, "Documents reviewed", size=12, bold=True)
    for row, document in enumerate(
        [
            dict(title="Title", reference="Reference", revision="Revision"),
            *project.get("documents", []),
        ],
        14,
    ):
        for first, last, field in ((1, 4, "title"), (5, 6, "reference"), (7, 8, "revision")):
            _cover_block(
                cover,
                row,
                first,
                last,
                document.get(field, ""),
                ACCENT if row == 14 else None,
                bold=row == 14,
            )
        for cell in cover[row]:
            cell.border = BORDER
            if row > 14 and row % 2:
                cell.fill = PatternFill("solid", fgColor="F2F2F2")
        cover.row_dimensions[row].height = 30
    tile_row = cover.max_row + 3
    _cover_block(cover, tile_row - 1, 1, 8, "Issues by severity", size=12, bold=True)
    for index, (severity, color) in enumerate(zip(SEVERITIES, COLORS, strict=True)):
        first = index * 2 + 1
        _cover_block(cover, tile_row, first, first + 1, totals[severity], color, 20, True)
        _cover_block(cover, tile_row + 1, first, first + 1, severity.capitalize(), color)
    cover.row_dimensions[tile_row].height = 36
    cover.row_dimensions[tile_row + 1].height = 24
    _cover_block(cover, tile_row + 3, 1, 2, len(issues), ACCENT, 20, True)
    _cover_block(cover, tile_row + 4, 1, 2, "Total", ACCENT)
    cover.row_dimensions[tile_row + 3].height = 36
    cover.row_dimensions[tile_row + 4].height = 24
    _cover_block(
        cover,
        tile_row + 6,
        1,
        8,
        "Snapshots show affected elements in red; other elements are greyed out.",
    )
    cover.row_dimensions[tile_row + 6].height = 30
    _append(summary, ["Category", *[value.capitalize() for value in SEVERITIES], "Total"])
    categories = list(dict.fromkeys(issue["category"] for issue in issues))
    for category in categories:
        counts = [
            sum(issue["category"] == category and issue["severity"] == severity for issue in issues)
            for severity in SEVERITIES
        ]
        _append(summary, [category, *counts, sum(counts)])
    _append(summary, ["Total", *totals.values(), len(issues)])
    total_row = summary.max_row
    for cell in summary[total_row]:
        cell.font = Font(name="Calibri", size=10, bold=True)
        cell.border = Border(top=Side(style="thin", color="BFBFBF"))
    _append(summary, [])
    _append(summary, ["Status", "Issues"])
    _header(summary, row=summary.max_row, columns=2)
    for status, count in Counter(issue["status"] for issue in issues).items():
        _append(summary, [status, count])
    chart = BarChart()
    chart.type = "bar"
    chart.grouping = "stacked"
    chart.overlap = 100
    chart.title = "Issues by category"
    chart.width = 16
    chart.height = 9
    chart.legend.position = "b"
    chart.gapWidth = 60
    chart.add_data(
        Reference(summary, min_col=2, max_col=5, min_row=1, max_row=len(categories) + 1),
        titles_from_data=True,
    )
    chart.set_categories(Reference(summary, min_col=1, min_row=2, max_row=len(categories) + 1))
    for series, color in zip(chart.series, COLORS, strict=True):
        series.graphicalProperties.solidFill = color
    summary.add_chart(chart, "I2")
    _header(summary, [35, 15, 15, 15, 15, 15])
    _append(register, HEADERS)
    _append(elements, ["Issue ID", "Element ID"])
    snapshot_count = 0
    for index, issue in enumerate(issues):
        ids = issue["element_ids"]
        _append(
            register,
            [
                issue["id"],
                notes.get(index, ""),
                issue["title"],
                issue["severity"],
                issue["category"],
                issue.get("requirement_source", ""),
                issue.get("requirement", ""),
                issue["finding"],
                issue.get("recommendation", ""),
                issue["status"],
                issue.get("responsible", ""),
                issue.get("due", ""),
                len(ids),
                ", ".join(map(str, ids[:30]))
                + (f" +{len(ids) - 30} more" if len(ids) > 30 else ""),
            ],
        )
        row = register.max_row
        severity_cell = register.cell(row, 4)
        severity_cell.fill = PatternFill(
            "solid", fgColor=COLORS[SEVERITIES.index(issue["severity"])]
        )
        severity_cell.font = Font(
            name="Calibri",
            size=10,
            bold=True,
            color="FFFFFF" if issue["severity"] == "critical" else "000000",
        )
        if issue["status"] == "Open":
            register.cell(row, 10).fill = PatternFill("solid", fgColor="FCE4E4")
        register.row_dimensions[row].height = 60
        if index in snapshots and index not in skipped:
            try:
                image = _snapshot(snapshots[index])
            except (OSError, ValueError) as error:
                message = f"Snapshot unavailable: {error}"
                register.cell(row, 2, message)
                warnings.append(f"{issue['id']}: {message}")
                image = None
            if image is not None:
                image.anchor = OneCellAnchor(
                    _from=AnchorMarker(
                        col=1, row=row - 1, colOff=pixels_to_EMU(4), rowOff=pixels_to_EMU(3)
                    ),
                    ext=XDRPositiveSize2D(pixels_to_EMU(360), pixels_to_EMU(240)),
                )
                register.add_image(image)
                register.row_dimensions[row].height = 185
                snapshot_count += 1
        elif ids and issue["snapshot"] != "none" and index not in notes:
            message = "Snapshot unavailable: no image supplied"
            register.cell(row, 2, message)
            warnings.append(f"{issue['id']}: {message}")
        for element_id in ids:
            _append(elements, [issue["id"], element_id])
    _header(register, [9, 52, 32, 11, 20, 16, 40, 46, 36, 10, 16, 11, 9, 24], filter_rows=True)
    register.freeze_panes = "C2"
    register.print_title_rows = "1:1"
    _header(elements, [20, 20], filter_rows=True)
    cover.sheet_view.showGridLines = False
    summary.sheet_view.showGridLines = False
    for sheet in workbook:
        sheet.page_setup.orientation = "landscape"
        sheet.page_setup.paperSize = sheet.PAPERSIZE_A3 if sheet == register else sheet.PAPERSIZE_A4
        sheet.page_setup.fitToWidth = 1
        sheet.page_setup.fitToHeight = 1 if sheet == cover else 0
        sheet.sheet_properties.pageSetUpPr.fitToPage = True
        sheet.oddFooter.left.text = project.get("name", "").replace("&", "&&")
        sheet.oddFooter.right.text = "Page &P of &N"
        sheet.print_options.horizontalCentered = True
        sheet.print_area = sheet.dimensions
    summary.print_area = f"A1:R{max(summary.max_row, 20)}"
    target.parent.mkdir(parents=True, exist_ok=True)
    try:
        with target.open("xb") as output:
            workbook.save(output)
    except FileExistsError as error:
        raise ValueError(f"output_path already exists: {target}") from error
    return {
        "path": str(target),
        "issueCount": len(issues),
        "snapshotCount": snapshot_count,
        "bySeverity": totals,
        "warnings": warnings,
    }
