# Contributing

Bug reports and feature requests use the [issue forms](https://github.com/sharafutdinovdi/revit-model-mcp/issues/new/choose).
Bug reports include the Revit year, add-in version, installation method, transport, reproduction steps and sanitized logs.
For visible failures, attach a screenshot of the dialog or ribbon in the form's screenshot field.
Usage and installation questions belong in [Discussions](https://github.com/sharafutdinovdi/revit-model-mcp/discussions).

Contributors follow the [code of conduct](CODE_OF_CONDUCT.md).

## Pull requests

1. Open an issue before a large change and agree on the expected behavior.
2. Fork the repository, clone the fork and create a focused branch from `main`, for example `git switch -c fix/http-timeout`.
3. Make the change and run the relevant checks below.
4. Use Conventional Commits for commits and the PR title, for example `fix(server): handle connection timeouts` or `feat(addin): expose view metadata`.
5. Push the branch and open a PR against `main`; fill in the summary, linked issue and checklist.
6. For any UI or ribbon change, drag and drop a screenshot or recording into the PR's validation section, or paste an image from the clipboard.
7. Remove credentials, private model names and paths from attachments and logs.

The PR checklist covers supported Revit builds, tests, documentation, screenshots and secrets.
Include commands, results and affected Revit years in the validation section; state when a checklist item does not apply.
Use English for code and public API descriptions.

`main` requires a PR and passing required checks on an up-to-date branch.
There is no blanket approval requirement.
Owner review applies to the paths in [CODEOWNERS](.github/CODEOWNERS):

- Add-in and Core source under `src/`, except project dependency manifests.
- Python runtime code under `server/revit_model_mcp/`.
- Installer code under `build/install/` and the root installation script.
- Release, release-please, WinGet and Dependabot auto-merge workflows.
- CODEOWNERS, the security policy and the license.

Docs, tests, other CI workflows and dependency manifests without an owner can merge after required checks pass.
Dependabot patch and minor updates enable squash auto-merge; required checks and any owner review still apply.
Major updates receive a `needs-review` label and wait for a maintainer.
NuGet manifests under `build/install/` and publishing workflow updates still require owner review.
All merges use squash with the PR title and body, and history remains linear.
CI builds the Revit 2022, 2026 and 2027 add-ins, runs Core and Python tests, builds and smoke-tests both MSI scopes, and validates the Python package.
PR checks validate the Conventional Commit title, all workflow files with `actionlint`, C# formatting from `.editorconfig`, and Python lint and formatting with Ruff.
CodeQL analyzes C# and Python on PRs, pushes to `main` and a weekly schedule.
Successful PR checks publish one updated comment with add-in artifact links and the Revit years built.
Artifact downloads require a GitHub login and expire after 90 days.
Path labels are applied automatically; `enhancement`, `bug`, `docs` and `dependencies` group generated release notes.

## Build and test

Run from a fresh clone's repository root on Windows with the .NET SDK selected by `global.json`:

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

`DeployAddin=false` prevents deployment to the local Revit installation.
Core tests need no running Revit instance.
The test runner is Microsoft.Testing.Platform; use `--project` as shown.
Release builds cover `Release.R22` through `Release.R27`.
Run `dotnet format RevitModelMcp.sln` with the same environment variables to apply formatting.

Run Python tests and package builds on Windows, macOS or Linux with Python 3.11+ and uv:

```sh
cd server
uv run --with pytest pytest -q
uvx ruff==0.16.7 check .
uvx ruff==0.16.7 format --check .
uv build
uvx twine check dist/*
```

Transport tests use mocked operations and a local fake HTTP server.
They do not require a Windows workstation.
Keep credentials and model files out of commits and use sanitized fixtures.
Run `uvx ruff==0.16.7 check --fix .` and `uvx ruff==0.16.7 format .` to apply Python lint fixes and formatting.
Run `actionlint` 1.7.12 from the repository root after changing a workflow.

## Documentation site

The documentation site uses MkDocs Material and includes the repository and server READMEs.
Preview it from the repository root:

```sh
python3 -m venv .venv
.venv/bin/python -m pip install -r docs/requirements.txt
.venv/bin/mkdocs serve
```

On Windows, use `.venv\Scripts\python.exe` and `.venv\Scripts\mkdocs.exe`.
Open `http://127.0.0.1:8000/revit-model-mcp/`.
Run `mkdocs build --strict` in the activated environment before submitting a PR.

## Test coverage

The Python tests cover job construction, transport failures, downloads, action validation and MCP stdio registration with both flag states.
A threaded fake HTTP server covers health, authentication, busy responses, job polling and PNG download.
Core tests cover parsing, serialization, formatting, units and query processing.
These tests do not require a live Revit model.

Automated tests do not validate live Revit behavior; see [validation evidence](docs/validation.md).

## Release ritual

1. Merge PRs with Conventional Commit titles.
2. release-please maintains a `chore(main): release X.Y.Z` PR with generated changelog entries and version updates.
3. The maintainer checks the release PR and merges it after required checks pass.
4. Check the Release please workflow, both MSI assets, six ZIPs, wheel, source distribution and `SHA256SUMS.txt`.
5. Check PyPI, MCP Registry and WinGet job results for stable releases; download the manifests if WinGet submission is not configured.

release-please owns [CHANGELOG.md](CHANGELOG.md), the version in `server/pyproject.toml` and both versions in `server/server.json`.
The manifest starts at `0.3.0`; `server/pyproject.toml` remains the package version checked by the build.
The simple strategy skips its absent default `version.txt`; no separate version file is maintained.
Before 1.0, `feat` and breaking changes bump the minor version, while `fix` bumps the patch version.
Visible documentation and dependency changes can also produce a patch release.
The `bump-patch-for-minor-pre-major` option is false to retain minor bumps for features.

Merging the release PR creates `vX.Y.Z` and a GitHub Release with the generated notes.
The workflow calls the existing build and publish pipeline directly; tags created with `GITHUB_TOKEN` do not trigger tag-push workflows.
The pipeline appends an Install section and replaces only that section on retries.
A manually pushed tag also runs the pipeline and uses GitHub-generated notes if no release exists.
The tag version must equal `server/pyproject.toml`.

Release PRs created or updated with `GITHUB_TOKEN` do not trigger pull request checks.
The maintainer closes and reopens the release PR after its latest update to start those checks before merging.
Repository Actions settings must allow GitHub Actions to create pull requests.

## Release assets

CI uploads installable R22, R26 and R27 folder layouts and an `installers` artifact.
It extracts both MSIs, rejects Revit API assemblies, and checks installation and removal for each built year.
The release-please workflow or a manually pushed `v<version>` tag triggers all six add-in builds and Core/server tests.
The tag version must match `server/pyproject.toml`.
The GitHub Release contains six per-year ZIPs, single-user and multi-user MSIs, the Python wheel and source distribution, and `SHA256SUMS.txt` covering every asset.
Extract each year's ZIP into `%APPDATA%\Autodesk\Revit\Addins\20<yy>` while that Revit instance is closed.
The archive root contains `RevitModelMcp.addin` and the `RevitModelMcp` assembly directory.

The release workflow invokes the WixSharp pipeline with `Build__Version` set to the tag version.
CI uses `0.0.0-ci`.
After building the required years on Windows, the same packaging command is available locally:

```powershell
$env:Build__Version = '0.2.0'
dotnet run --project build -- pack --no-build
```

`pack --no-build` packages existing `src/RevitModelMcp.Addin/bin/Release.R*` directories without cleaning or recompiling them.
Use a fresh checkout for release packaging to exclude stale configurations.
The module installs WiX 7, accepts its EULA, installs the matching UI extension, and writes the MSIs to `output/`.
Single-user installation uses `%APPDATA%\Autodesk\Revit\Addins\<year>`.
Multi-user installation uses `%ProgramData%\Autodesk\Revit\Addins\<year>` through 2026 and `%ProgramFiles%\Autodesk\Revit\Addins\2027` for 2027.

Stable releases publish the wheel and source distribution to PyPI through GitHub OIDC in the `pypi` environment.
The pending publisher configuration is listed above the `pypi` job in `.github/workflows/release.yml`.
PyPI failure does not block the GitHub Release.
After PyPI succeeds, the workflow updates both versions in `server/server.json` and publishes to the official MCP Registry through GitHub OIDC.
Registry publishing is best-effort and its response appears in the job log.
Prerelease tags containing `-` skip PyPI, MCP Registry and WinGet publishing.

The release workflow calls `.github/workflows/winget.yml` after publishing the GitHub Release.
The nested `release-please.yml` to `release.yml` to `winget.yml` chain uses supported reusable workflow nesting.
The calling job grants `id-token: write`; the PyPI job retains the `pypi` environment and both publishers retain OIDC permissions.
WinGet also supports manually published releases and `workflow_dispatch` with a stable release tag.
It generates and validates manifests for `Sharafutdinov.RevitModelMcp` and uploads a `winget-manifests` artifact.
Submission requires the optional `WINGET_TOKEN` repository secret, a classic PAT with `public_repo` scope.
Without the token, generation and artifact upload still run.
On Windows, `build/winget/New-WingetManifests.ps1 -Version 0.2.0 -ReleaseTag v0.2.0 -OutputDir artifacts/winget` downloads the MSIs and reads their hashes and product codes.
`-SkipDownload` uses matching MSIs already in `output/`.

Contributions are licensed under the [MIT license](LICENSE).
