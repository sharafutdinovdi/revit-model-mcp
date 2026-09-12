"""MCP tools for reading a live Autodesk Revit model."""

from importlib.metadata import PackageNotFoundError
from importlib.metadata import version as _version


def package_version() -> str:
    try:
        return _version("revit-model-mcp")
    except PackageNotFoundError:
        return "0.0.0+unknown"
