# Agent instructions

## Purpose and runtime

Revit Model MCP exposes live Revit model data to MCP clients, read-only by default.
The C# add-in runs inside Revit on Windows and owns Revit API access through ExternalEvent.
The Python server runs on the MCP client's machine and exposes stdio tools over local, SSH or HTTP transport to the add-in.

## Repository map

| Path | Responsibility |
| --- | --- |
| `src/` | C# add-in and Revit-independent Core library |
| `tests/` | C# Core tests |
| `server/` | Python MCP server, tests, package metadata and registry manifest |
| `build/` | Build orchestration, WixSharp packaging and WinGet manifests |
| `docs/` | Tool contracts, transports, architecture and validation evidence |
| `.github/` | CI, release workflows, issue forms and PR template |

## Build and test

Use the SDK in `global.json`.
Run from the repository root on Windows:

```powershell
foreach ($year in '22', '23', '24', '25', '26', '27') {
    dotnet build src/RevitModelMcp.Addin -c "Release.R$year" -p:DeployAddin=false
    if ($LASTEXITCODE -ne 0) { throw "R$year build failed" }
}
dotnet test --project tests/RevitModelMcp.Core.Tests/RevitModelMcp.Core.Tests.csproj
$env:Configuration = 'Debug.R26'
$env:DeployAddin = 'false'
dotnet format RevitModelMcp.sln --verify-no-changes --verbosity minimal
```

Core tests require no running Revit instance and can run on macOS or Linux.
The test runner uses Microsoft.Testing.Platform; keep the `--project` argument.
Keep `Configuration=Debug.R26` and `DeployAddin=false` set when applying `dotnet format RevitModelMcp.sln`.
Windows builds and MSI checks use PR CI as the oracle when working on macOS.

For Python 3.11+ with uv, from the repository root:

```sh
cd server
uv run --with pytest pytest -q
uvx ruff==0.16.7 check .
uvx ruff==0.16.7 format --check .
uv build
uvx twine check dist/*
```

Apply formatting with `uvx ruff==0.16.7 format .` in `server/`.
Run `actionlint` 1.7.12 from the repository root after workflow changes and before pushing them.
See [CONTRIBUTING.md](CONTRIBUTING.md) for packaging commands and test coverage.

## Conventions

- Use Conventional Commits for commits and PR titles.
- Write code, comments and documentation in English with sentence-case headings and short sentences.
- Add or update tests for every behavior change; never delete or weaken existing tests.
- Update the Unreleased section in [CHANGELOG.md](CHANGELOG.md).
- Follow `.editorconfig` and the pinned Ruff configuration.
- Keep internal documentation links valid when moving sections.

## Hard rules

- Keep MCP actions behind both `REVIT_MCP_ALLOW_WRITE=1` and the workstation `allow-write` gate.
- Never widen defaults toward writing; direct HTTP actions still require authentication and the workstation gate.
- Never log model paths when redaction is on, or expose secrets and confidential model data in commits or logs.
- Never include Revit API assemblies in artifacts.
- Keep transports loopback-first; remote exposure requires explicit configuration.
- Preserve timeout and verification warnings: a timed-out or failed verification response may follow a committed change.
- Never run `git reset --hard`, `git clean -fd` or force push.

## Release ritual

Follow the [release ritual](CONTRIBUTING.md#release-ritual).
The version in `server/pyproject.toml` must equal the tag without its `v` prefix.
Move Unreleased entries into a dated version section before tagging; missing highlights fail publication.
Verify assets and publishing jobs after the release workflow finishes.
