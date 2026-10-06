import errno

import pytest

from revit_model_mcp import atomic_write
from revit_model_mcp.atomic_write import write_new_file


def test_success(tmp_path):
    target = tmp_path / "capture.png"
    write_new_file(target, lambda output: output.write(b"content"))
    assert target.read_bytes() == b"content"
    assert list(tmp_path.iterdir()) == [target]


def test_existing_target(tmp_path):
    target = tmp_path / "capture.png"
    target.write_bytes(b"original")
    with pytest.raises(ValueError, match=f"Local file already exists: {target}"):
        write_new_file(target, lambda output: output.write(b"replacement"))
    assert target.read_bytes() == b"original"
    assert list(tmp_path.iterdir()) == [target]


def test_writer_failure(tmp_path):
    target = tmp_path / "capture.png"

    def fail(output):
        output.write(b"partial")
        raise RuntimeError("write failed")

    with pytest.raises(RuntimeError, match="write failed"):
        write_new_file(target, fail)
    assert not target.exists()
    assert list(tmp_path.iterdir()) == []


def test_unsupported_link_fallback(tmp_path, monkeypatch):
    target = tmp_path / "capture.png"

    def unsupported_link(source, destination):
        assert target.exists() is False or target.read_bytes() == b"original"
        assert source != destination
        raise OSError(errno.EXDEV, "Hard links unsupported")

    monkeypatch.setattr(atomic_write.os, "link", unsupported_link)
    write_new_file(target, lambda output: output.write(b"content"))
    assert target.read_bytes() == b"content"
    assert list(tmp_path.iterdir()) == [target]
    target.write_bytes(b"original")
    with pytest.raises(ValueError, match="Local file already exists"):
        write_new_file(target, lambda output: output.write(b"replacement"))
    assert target.read_bytes() == b"original"
    assert list(tmp_path.iterdir()) == [target]
