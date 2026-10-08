from __future__ import annotations

import runpy
from pathlib import Path
from unittest.mock import patch
from zipfile import ZipFile

import pytest

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
validator = runpy.run_path(str(REPOSITORY_ROOT / "build" / "validate_bundle.py"))
validate = validator["validate"]
main = validator["main"]

YEARS = list(range(2022, 2028))
PRODUCT_CODE = "{1A2B3C4D-0000-4000-8000-0123456789AB}"
UPGRADE_CODE = "{6C0E2B3D-52A1-4F5E-9B7A-3D1C8E4A7F21}"


def component_xml(year: int, series_max: str | None = None) -> str:
    return (
        f'  <Components Description="Revit {year}">\n'
        f'    <RuntimeRequirements OS="Win64" Platform="Revit" SeriesMin="R{year}" '
        f'SeriesMax="{series_max or f"R{year}"}" />\n'
        f'    <ComponentEntry AppName="Revit Model MCP" '
        f'ModuleName="./Contents/{year}/RevitModelMcp.addin" />\n'
        "  </Components>\n"
    )


def manifest_xml(
    years: list[int] = YEARS,
    app_version: str = "0.9.1",
    product_code: str | None = PRODUCT_CODE,
    upgrade_code: str = UPGRADE_CODE,
    url: str = "https://example.com",
    components: str | None = None,
) -> str:
    product = f' ProductCode="{product_code}"' if product_code is not None else ""
    body = components if components is not None else "".join(component_xml(y) for y in years)
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="Revit" '
        'ProductType="Application" Name="Revit Model MCP" Description="Read live models." '
        f'AppVersion="{app_version}" FriendlyVersion="{app_version}"{product} '
        f'UpgradeCode="{upgrade_code}" Author="Dinar">\n'
        f'  <CompanyDetails Name="Dinar" Url="{url}" Email="a@b.c" />\n'
        f"{body}</ApplicationPackage>\n"
    )


def addin_xml() -> str:
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n<RevitAddIns><AddIn Type="Application">'
        "<Assembly>RevitModelMcp\\RevitModelMcp.dll</Assembly></AddIn></RevitAddIns>\n"
    )


def bundle_files(prefix: str = "RevitModelMcp.bundle/", **manifest: object) -> dict[str, str]:
    files = {f"{prefix}PackageContents.xml": manifest_xml(**manifest)}  # type: ignore[arg-type]
    for year in YEARS:
        files[f"{prefix}Contents/{year}/RevitModelMcp.addin"] = addin_xml()
        files[f"{prefix}Contents/{year}/RevitModelMcp/RevitModelMcp.dll"] = "dll"
    return files


def write_zip(tmp_path: Path, files: dict[str, str]) -> Path:
    archive = tmp_path / "bundle.zip"
    with ZipFile(archive, "w") as zipped:
        for name, content in files.items():
            zipped.writestr(name, content)
    return archive


def test_valid_zip(tmp_path: Path) -> None:
    assert validate(write_zip(tmp_path, bundle_files()), YEARS) == []


@pytest.mark.parametrize("version", ["0.9.1", "0.9.1-rc.1"])
def test_valid_managed_zip(tmp_path: Path, version: str) -> None:
    files = bundle_files()
    launcher = "uvx --prerelease=allow" if "-" in version else "uvx"
    files["RevitModelMcp.bundle/README.txt"] = f"{launcher} revit-model-mcp=={version}\n"
    assert validate(write_zip(tmp_path, files), YEARS, managed_version=version) == []


def test_managed_zip_needs_readme(tmp_path: Path) -> None:
    errors = validate(write_zip(tmp_path, bundle_files()), YEARS, managed_version="0.9.1")
    assert errors == ["Managed bundle needs README.txt"]


def test_managed_zip_needs_matching_server_version(tmp_path: Path) -> None:
    files = bundle_files()
    files["RevitModelMcp.bundle/README.txt"] = "uvx revit-model-mcp==0.9.0\n"
    errors = validate(write_zip(tmp_path, files), YEARS, managed_version="0.9.1")
    assert errors == ["README.txt does not pin revit-model-mcp==0.9.1"]


def test_valid_zip_with_manifest_at_root(tmp_path: Path) -> None:
    assert validate(write_zip(tmp_path, bundle_files(prefix="")), YEARS) == []


def test_valid_directory(tmp_path: Path) -> None:
    root = tmp_path / "extracted"
    for name, content in bundle_files().items():
        target = root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content)
    assert validate(root, YEARS) == []


def replace_manifest(files: dict[str, str], **manifest: object) -> dict[str, str]:
    files["RevitModelMcp.bundle/PackageContents.xml"] = manifest_xml(**manifest)  # type: ignore[arg-type]
    return files


def missing_year(files: dict[str, str]) -> dict[str, str]:
    return replace_manifest(files, years=YEARS[:-1])


def extra_year(files: dict[str, str]) -> dict[str, str]:
    return replace_manifest(files, years=[*YEARS, 2028])


def duplicate_year(files: dict[str, str]) -> dict[str, str]:
    return replace_manifest(files, years=[*YEARS, 2022])


def bad_series_max(files: dict[str, str]) -> dict[str, str]:
    components = component_xml(2022, "R2023") + "".join(component_xml(y) for y in YEARS[1:])
    return replace_manifest(files, components=components)


def module_backslash(files: dict[str, str]) -> dict[str, str]:
    components = component_xml(2022).replace("2022/RevitModelMcp", "2022\\RevitModelMcp")
    components += "".join(component_xml(y) for y in YEARS[1:])
    return replace_manifest(files, components=components)


def module_missing(files: dict[str, str]) -> dict[str, str]:
    del files["RevitModelMcp.bundle/Contents/2022/RevitModelMcp.addin"]
    return files


def assembly_missing(files: dict[str, str]) -> dict[str, str]:
    del files["RevitModelMcp.bundle/Contents/2023/RevitModelMcp/RevitModelMcp.dll"]
    return files


def revit_api_member(files: dict[str, str]) -> dict[str, str]:
    files["RevitModelMcp.bundle/Contents/2022/RevitModelMcp/RevitAPI.dll"] = "dll"
    return files


def traversal_member(files: dict[str, str]) -> dict[str, str]:
    files["RevitModelMcp.bundle/../evil.txt"] = "x"
    return files


@pytest.mark.parametrize(
    ("mutate", "fragment"),
    [
        pytest.param(missing_year, "2027", id="missing-year"),
        pytest.param(extra_year, "Unexpected", id="extra-year"),
        pytest.param(duplicate_year, "expected one", id="duplicate-year"),
        pytest.param(bad_series_max, "SeriesMax", id="bad-series-max"),
        pytest.param(
            lambda f: replace_manifest(f, app_version="1.0.0-rc.1"), "AppVersion", id="prerelease"
        ),
        pytest.param(
            lambda f: replace_manifest(f, product_code=None), "ProductCode", id="no-product-code"
        ),
        pytest.param(
            lambda f: replace_manifest(f, product_code=PRODUCT_CODE.lower()),
            "ProductCode",
            id="lowercase-guid",
        ),
        pytest.param(
            lambda f: replace_manifest(f, upgrade_code=UPGRADE_CODE.strip("{}")),
            "UpgradeCode",
            id="unbraced-guid",
        ),
        pytest.param(lambda f: replace_manifest(f, url="http://example.com"), "Url", id="http-url"),
        pytest.param(module_backslash, "backslash", id="module-backslash"),
        pytest.param(module_missing, "does not exist", id="module-missing"),
        pytest.param(assembly_missing, "Assembly target", id="assembly-missing"),
        pytest.param(revit_api_member, "RevitAPI.dll", id="revit-api"),
        pytest.param(traversal_member, "Unsafe", id="traversal"),
    ],
)
def test_invalid_bundle(tmp_path: Path, mutate, fragment: str) -> None:
    errors = validate(write_zip(tmp_path, mutate(bundle_files())), YEARS)
    assert any(fragment in error for error in errors), errors


def test_main_success(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    archive = write_zip(tmp_path, bundle_files())
    with patch("sys.argv", ["validate_bundle.py", str(archive)]):
        with pytest.raises(SystemExit) as exit_info:
            main()
    assert exit_info.value.code == 0
    assert capsys.readouterr().out.startswith("OK")


def test_main_failure(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    archive = write_zip(tmp_path, missing_year(bundle_files()))
    with patch("sys.argv", ["validate_bundle.py", str(archive), "--years", "2022-2027"]):
        with pytest.raises(SystemExit) as exit_info:
            main()
    assert exit_info.value.code == 1
    assert "ERROR" in capsys.readouterr().out


def test_main_managed_success(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    files = bundle_files()
    files["RevitModelMcp.bundle/README.txt"] = "uvx revit-model-mcp==0.9.1\n"
    archive = write_zip(tmp_path, files)
    with patch("sys.argv", ["validate_bundle.py", str(archive), "--managed-version", "0.9.1"]):
        with pytest.raises(SystemExit) as exit_info:
            main()
    assert exit_info.value.code == 0
    assert capsys.readouterr().out.startswith("OK")
