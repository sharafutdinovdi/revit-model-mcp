Installers: follow [Install with an AI agent](docs/agent-install.md). This file is the contributor guide.

# Agent instructions

## Purpose and runtime

Model MCP exposes live Revit models to MCP clients. Actions are enabled by default, and read-only mode stays available.
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

Install local hooks and run all checks from the repository root:

```sh
uv tool install pre-commit
pre-commit install
pre-commit run --all-files
```

The C# hook prints a notice and exits successfully on macOS and Linux; Windows PR CI verifies C# formatting.
See [Local checks](CONTRIBUTING.md#local-checks) for prerequisites and hook behavior on each platform.
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
uv run --with pytest --with hypothesis --with lxml==6.1.3 pytest -q
uvx ruff==0.17.3 check .
uvx ruff==0.17.3 format --check .
uv build
uvx twine check dist/*
```

Apply formatting with `uvx ruff==0.17.3 format .` in `server/`.
Run `actionlint` 1.7.12 from the repository root after workflow changes and before pushing them.
See [CONTRIBUTING.md](CONTRIBUTING.md) for packaging commands and test coverage.

## Conventions

- Use Conventional Commits for commits and PR titles.
- Write code, comments and documentation in English with sentence-case headings and short sentences.
- Add or update tests for every behavior change; never delete or weaken existing tests.
- Tests assert behavior through the public surface. A Python test never reads C# source files to check their text; add-in behavior without a Core seam is verified on a Revit workstation and noted in the PR.
- Describe release changes in Conventional Commit messages; release-please updates [CHANGELOG.md](CHANGELOG.md).
- Follow `.editorconfig` and the pinned Ruff configuration.
- Keep internal documentation links valid when moving sections.

## Hard rules

- Actions are enabled by default; transactional model edits use one named undo entry and completed actions report an activity summary. Session operations, saves, sync, exports, link removal and saved-tool file changes are not covered by model Undo. Read-only mode must stay available through the shared workstation lock, controlled by the Model MCP ribbon or `revit_set_read_only`. Chat unlock requires explicit user confirmation. Machine policy cannot be lifted by ribbon or chat.
- Direct HTTP actions still require authentication and are refused while the workstation read-only lock is on. The authenticated `set-read-only` command remains available while locked.
- Never log model paths when redaction is on, or expose secrets and confidential model data in commits or logs.
- Never include Revit API assemblies in artifacts.
- Keep transports loopback-first; remote exposure requires explicit configuration.
- Preserve timeout and verification warnings: a timed-out or failed verification response may follow a committed change.
- Preserve confirmation binding to the exact execution intent, including `dry_run`; first saved-tool runs require confirmation even for read mode and previews.
- Keep source-model read-only guarantees separate from batch orchestration permissions: batch start and cancel are refused while locked.
- Preserve nested and top-level `skipped`/`skippedCount`, truncation and paging diagnostics; incomplete evidence is not a full-population pass.
- Preserve write-new report and download destinations, request journal recovery, dialog diagnostics and progress notifications; progress is not proof of completion.
- Maintain the [safety contract evidence](docs/actions.md#contract-evidence) when changing these guarantees.
- Never run `git reset --hard`, `git clean -fd` or force push.

## Release ritual

Follow the [release ritual](CONTRIBUTING.md#release-ritual).
The version in `server/pyproject.toml` must equal the tag without its `v` prefix.
Agents never edit `CHANGELOG.md`, the `server/pyproject.toml` version or either `server/server.json` version by hand.
release-please owns these files and version fields through its release PR.
The maintainer merges the release PR to create the tag and publish the release.
Verify assets and publishing jobs after the release workflow finishes.
