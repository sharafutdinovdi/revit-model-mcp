"""Validate an Autodesk .bundle package (zip archive or extracted directory)."""

from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath
from zipfile import BadZipFile, ZipFile

MANIFEST_NAME = "PackageContents.xml"
VERSION_PATTERN = re.compile(r"^\d+\.\d+\.\d+(\.\d+)?$")
GUID_PATTERN = re.compile(r"^\{[0-9A-F]{8}(-[0-9A-F]{4}){3}-[0-9A-F]{12}\}$")
FORBIDDEN_PATTERN = re.compile(
    r"^(AdWindows|UIFramework|RevitAPI|RevitNET).*\.dll$", re.IGNORECASE
)


def parse_years(text: str) -> list[int]:
    """Parse `2022-2027` or `2022,2023` into a list of years."""
    if "-" in text:
        first, last = text.split("-", 1)
        return list(range(int(first), int(last) + 1))
    return [int(part) for part in text.split(",") if part.strip()]


def list_members(path: Path) -> tuple[list[str], dict[str, bytes] | None]:
    """Return file member names with forward slashes and, for zips, a name to bytes map."""
    if path.is_dir():
        names = [
            item.relative_to(path).as_posix()
            for item in sorted(path.rglob("*"))
            if item.is_file()
        ]
        return names, None
    contents: dict[str, bytes] = {}
    with ZipFile(path) as archive:
        for info in archive.infolist():
            name = info.filename.replace("\\", "/")
            if not name.endswith("/"):
                contents[name] = archive.read(info)
    return list(contents), contents


def read_member(path: Path, contents: dict[str, bytes] | None, name: str) -> bytes:
    if contents is not None:
        return contents[name]
    return (path / name).read_bytes()


def is_unsafe(name: str) -> bool:
    parts = name.split("/")
    return name.startswith("/") or ".." in parts or bool(re.match(r"^[A-Za-z]:", name))


def find_base(names: list[str]) -> str | None:
    """Return the prefix (empty or `X.bundle/`) that holds PackageContents.xml."""
    if MANIFEST_NAME in names:
        return ""
    tops = {name.split("/", 1)[0] for name in names if "/" in name}
    if len(tops) == 1:
        top = next(iter(tops))
        if top.endswith(".bundle") and f"{top}/{MANIFEST_NAME}" in names:
            return f"{top}/"
    return None


def parse_xml(data: bytes) -> ET.Element | None:
    try:
        return ET.fromstring(data)
    except ET.ParseError:
        return None


def check_package(root: ET.Element, errors: list[str]) -> None:
    if root.tag != "ApplicationPackage":
        errors.append(f"Root element is {root.tag}, expected ApplicationPackage")
        return
    expected = {
        "SchemaVersion": "1.0",
        "AutodeskProduct": "Revit",
        "ProductType": "Application",
    }
    for attribute, value in expected.items():
        if root.get(attribute) != value:
            errors.append(
                f"{attribute} must be {value!r}, found {root.get(attribute)!r}"
            )
    for attribute in ("Name", "Description", "Author"):
        if not (root.get(attribute) or "").strip():
            errors.append(f"{attribute} is missing or empty")
    if not VERSION_PATTERN.match(root.get("AppVersion") or ""):
        errors.append(f"AppVersion must be numeric, found {root.get('AppVersion')!r}")
    for attribute in ("ProductCode", "UpgradeCode"):
        if not GUID_PATTERN.match(root.get(attribute) or ""):
            errors.append(
                f"{attribute} must be an uppercase braced GUID, found {root.get(attribute)!r}"
            )
    company = root.find("CompanyDetails")
    if company is None:
        errors.append("CompanyDetails is missing")
        return
    if not (company.get("Name") or "").strip():
        errors.append("CompanyDetails Name is missing or empty")
    if not (company.get("Url") or "").startswith("https://"):
        errors.append(
            f"CompanyDetails Url must start with https://, found {company.get('Url')!r}"
        )
    if "@" not in (company.get("Email") or ""):
        errors.append(f"CompanyDetails Email is invalid: {company.get('Email')!r}")


def check_component(
    component: ET.Element,
    year: int,
    base: str,
    names: set[str],
    path: Path,
    contents: dict[str, bytes] | None,
    errors: list[str],
) -> None:
    label = f"Revit {year}"
    requirements = component.find("RuntimeRequirements")
    if requirements is None:
        errors.append(f"{label}: RuntimeRequirements is missing")
    else:
        if requirements.get("OS") != "Win64":
            errors.append(
                f"{label}: OS must be Win64, found {requirements.get('OS')!r}"
            )
        if requirements.get("Platform") != "Revit":
            errors.append(
                f"{label}: Platform must be Revit, found {requirements.get('Platform')!r}"
            )
        for attribute in ("SeriesMin", "SeriesMax"):
            if requirements.get(attribute) != f"R{year}":
                errors.append(
                    f"{label}: {attribute} must be R{year}, found {requirements.get(attribute)!r}"
                )
    entries = component.findall("ComponentEntry")
    if len(entries) != 1:
        errors.append(
            f"{label}: expected exactly one ComponentEntry, found {len(entries)}"
        )
        return
    entry = entries[0]
    if not (entry.get("AppName") or "").strip():
        errors.append(f"{label}: AppName is missing or empty")
    module = entry.get("ModuleName") or ""
    prefix = f"./Contents/{year}/"
    if not module.startswith(prefix):
        errors.append(f"{label}: ModuleName must start with {prefix}, found {module!r}")
    if not module.endswith(".addin"):
        errors.append(f"{label}: ModuleName must end with .addin, found {module!r}")
    if "\\" in module:
        errors.append(f"{label}: ModuleName must not contain backslashes: {module!r}")
        return
    target = base + module.removeprefix("./")
    if target not in names:
        errors.append(f"{label}: ModuleName target does not exist: {module!r}")
        return
    check_addin(label, target, names, path, contents, errors)


def check_addin(
    label: str,
    target: str,
    names: set[str],
    path: Path,
    contents: dict[str, bytes] | None,
    errors: list[str],
) -> None:
    addin = parse_xml(read_member(path, contents, target))
    if addin is None:
        errors.append(f"{label}: {target} is not valid XML")
        return
    assembly = addin.find(".//Assembly")
    if assembly is None or not (assembly.text or "").strip():
        errors.append(f"{label}: {target} has no Assembly element")
        return
    relative = (assembly.text or "").strip().replace("\\", "/")
    resolved = PurePosixPath(target).parent / relative
    if resolved.as_posix() not in names:
        errors.append(
            f"{label}: Assembly target does not exist: {assembly.text.strip()!r}"
        )


def validate(path: str | Path, years: list[int]) -> list[str]:
    """Return a list of error messages; an empty list means the bundle is valid."""
    path = Path(path)
    errors: list[str] = []
    try:
        names, contents = list_members(path)
    except (OSError, ValueError, BadZipFile) as error:  # unreadable or corrupt archive
        return [f"Cannot read {path}: {error}"]
    for name in names:
        if is_unsafe(name):
            errors.append(f"Unsafe member name: {name!r}")
    if errors:
        return errors
    for name in names:
        if FORBIDDEN_PATTERN.match(PurePosixPath(name).name):
            errors.append(f"Revit API assembly must not ship: {name}")
    base = find_base(names)
    if base is None:
        errors.append(
            f"{MANIFEST_NAME} not found at the root or in a single top-level .bundle folder"
        )
        return errors
    root = parse_xml(read_member(path, contents, base + MANIFEST_NAME))
    if root is None:
        errors.append(f"{MANIFEST_NAME} is not valid XML")
        return errors
    check_package(root, errors)
    components: dict[int, list[ET.Element]] = {}
    for component in root.findall("Components"):
        requirements = component.find("RuntimeRequirements")
        series = (
            requirements.get("SeriesMin") if requirements is not None else None
        ) or ""
        match = re.fullmatch(r"R(\d{4})", series)
        year = int(match.group(1)) if match else None
        if year is None:
            description = component.get("Description")
            match = re.search(r"\d{4}", description or "")
            year = int(match.group()) if match else -1
        components.setdefault(year, []).append(component)
    for year in sorted(set(components) - set(years)):
        errors.append(f"Unexpected Components for year {year}")
    name_set = set(names)
    for year in years:
        found = components.get(year, [])
        if not found:
            errors.append(f"Components for Revit {year} are missing")
        elif len(found) > 1:
            errors.append(f"Revit {year} has {len(found)} Components, expected one")
        else:
            check_component(found[0], year, base, name_set, path, contents, errors)
    return errors


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Validate an Autodesk .bundle package."
    )
    parser.add_argument("path", help="Bundle zip or extracted directory")
    parser.add_argument(
        "--years", default="2022-2027", help="Range 2022-2027 or list 2022,2023"
    )
    arguments = parser.parse_args()
    years = parse_years(arguments.years)
    errors = validate(arguments.path, years)
    if errors:
        for error in errors:
            print(f"ERROR: {error}")
        sys.exit(1)
    print("OK")
    print(
        f"{arguments.path}: {len(years)} Revit years ({years[0]}-{years[-1]}) validated"
    )
    sys.exit(0)


if __name__ == "__main__":
    main()
