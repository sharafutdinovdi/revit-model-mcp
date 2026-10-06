import errno
from pathlib import Path

import pytest
from openpyxl import Workbook

from revit_model_mcp.health_workbook import write_health_workbook
from revit_model_mcp.issue_register import write_register
from revit_model_mcp.snapshot_report import build_report

CURRENT = Path(__file__).parent / "fixtures" / "snapshots" / "current"


@pytest.fixture(params=["health", "register", "snapshot"])
def report_writer(request):
    if request.param == "health":
        return (
            lambda target: write_health_workbook(str(target), {"data": {"counts": {}}}, []),
            "save_to already exists",
        )
    if request.param == "register":
        return (
            lambda target: write_register(
                str(target),
                {},
                [
                    {
                        "title": "Missing code",
                        "category": "Classification",
                        "finding": "No code",
                        "severity": "major",
                    }
                ],
            ),
            "output_path already exists",
        )
    return (
        lambda target: build_report(str(CURRENT), str(target)),
        "Local file already exists",
    )


def test_save_failure_leaves_no_partial_report(tmp_path, monkeypatch, report_writer):
    write, _ = report_writer
    target = tmp_path / "report.xlsx"

    def fail_save(self, output):
        output.write(b"partial workbook")
        raise OSError(errno.ENOSPC, "No space left on device")

    monkeypatch.setattr(Workbook, "save", fail_save)
    with pytest.raises(ValueError, match="Cannot save report.*No space left on device"):
        write(target)
    assert not target.exists()
    assert list(tmp_path.iterdir()) == []


def test_existing_target_message_and_content(tmp_path, report_writer):
    write, message = report_writer
    target = tmp_path / "report.xlsx"
    target.write_bytes(b"original")
    with pytest.raises(ValueError, match=f"{message}: {target}"):
        write(target)
    assert target.read_bytes() == b"original"
    assert list(tmp_path.iterdir()) == [target]


def test_destination_created_during_save_is_preserved(tmp_path, monkeypatch, report_writer):
    write, message = report_writer
    target = tmp_path / "report.xlsx"

    def racing_save(self, output):
        output.write(b"new workbook")
        target.write_bytes(b"original")

    monkeypatch.setattr(Workbook, "save", racing_save)
    with pytest.raises(ValueError, match=f"{message}: {target}"):
        write(target)
    assert target.read_bytes() == b"original"
    assert list(tmp_path.iterdir()) == [target]
