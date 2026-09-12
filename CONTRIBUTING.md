# Contributing

Bug reports and feature requests use the [issue forms](https://github.com/sharafutdinovdi/revit-model-mcp/issues/new/choose).
Bug reports include the Revit year, add-in version, installation method, transport, reproduction steps and sanitized logs.
For visible failures, attach a screenshot of the dialog or ribbon in the form's screenshot field.
Usage and installation questions belong in [Discussions](https://github.com/sharafutdinovdi/revit-model-mcp/discussions).

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

`main` requires a PR, one approving review, and passing CI, PR checks and CodeQL for contributors.
Administrators can bypass these rules; force pushes remain disabled.
CI builds the Revit 2022 and 2026 add-ins, runs Core and Python tests, and builds the Python package.
PR checks validate the Conventional Commit title, all workflow files with `actionlint`, C# formatting from `.editorconfig`, and Python lint and formatting with Ruff.
CodeQL analyzes C# and Python on PRs, pushes to `main` and a weekly schedule.
Successful PR checks publish one updated comment with add-in artifact links and the Revit years built.
Artifact downloads require a GitHub login and expire after 90 days.
Path labels are applied automatically; `enhancement`, `bug`, `docs` and `dependencies` group generated release notes.

## Build and test

Run from a fresh clone's repository root on Windows with the .NET SDK selected by `global.json`:

```powershell
foreach ($year in '22', '23', '24', '25', '26') {
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
Release builds cover `Release.R22` through `Release.R26`.
Run `dotnet format RevitModelMcp.sln` with the same environment variables to apply formatting.

Run Python tests and package builds on Windows, macOS or Linux with Python 3.11+ and uv:

```sh
cd server
uv run --with pytest pytest -q
uvx ruff==0.16.7 check .
uvx ruff==0.16.7 format --check .
uv build
```

Transport tests use mocked operations and a local fake HTTP server.
They do not require a Windows workstation.
Keep credentials and model files out of commits and use sanitized fixtures.
Run `uvx ruff==0.16.7 check --fix .` and `uvx ruff==0.16.7 format .` to apply Python lint fixes and formatting.
Run `actionlint` 1.7.12 from the repository root after changing a workflow.

## Release assets

CI uploads installable R22 and R26 folder layouts after its tests pass.
A `v<version>` tag triggers all five add-in builds, Core/server tests and the Python wheel build.
The tag version must match `server/pyproject.toml`.
The release workflow attaches five ZIP files and the wheel to a GitHub Release with generated notes.
Extract each year's ZIP into `%APPDATA%\Autodesk\Revit\Addins\20<yy>` while that Revit instance is closed.
The archive root contains `RevitModelMcp.addin` and the `RevitModelMcp` assembly directory.
The workflow does not invoke the optional WiX installer pipeline.

Contributions are licensed under the [MIT license](LICENSE).
