from __future__ import annotations

import errno
import os
import shutil
import tempfile
from collections.abc import Callable
from pathlib import Path
from typing import BinaryIO


def write_new_file(target: Path, write: Callable[[BinaryIO], None]) -> None:
    descriptor, temporary = tempfile.mkstemp(prefix=f".{target.name}.", dir=target.parent)
    try:
        with os.fdopen(descriptor, "wb") as output:
            write(output)
            output.flush()
            os.fsync(output.fileno())
        try:
            os.link(temporary, target)
        except FileExistsError as error:
            raise ValueError(f"Local file already exists: {target}") from error
        except OSError as error:
            if error.errno not in {errno.EXDEV, errno.ENOSYS, errno.ENOTSUP, errno.EOPNOTSUPP}:
                raise
            try:
                output = open(target, "xb")
            except FileExistsError as conflict:
                raise ValueError(f"Local file already exists: {target}") from conflict
            identity = os.fstat(output.fileno())
            try:
                with output, open(temporary, "rb") as source:
                    shutil.copyfileobj(source, output)
                    output.flush()
                    os.fsync(output.fileno())
            except BaseException:
                try:
                    if os.path.samestat(identity, target.lstat()):
                        target.unlink()
                except FileNotFoundError:
                    pass
                raise
    finally:
        try:
            os.unlink(temporary)
        except FileNotFoundError:
            pass
