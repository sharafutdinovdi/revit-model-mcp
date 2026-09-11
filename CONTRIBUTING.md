# Contributing

Open an issue with the problem and expected behavior before a large change.
Keep pull requests focused and use Conventional Commits.
Include relevant test results and affected Revit versions.
Use English for code and public API descriptions.

## Build and test

Run from a fresh clone's repository root on Windows with the .NET SDK selected by `global.json`:

```powershell
dotnet build src/RevitModelMcp.Addin -c Release.R22 -p:DeployAddin=false
dotnet build src/RevitModelMcp.Addin -c Release.R26 -p:DeployAddin=false
dotnet test --project tests/RevitModelMcp.Core.Tests/RevitModelMcp.Core.Tests.csproj
```

`DeployAddin=false` prevents deployment to the local Revit installation.
Core tests need no running Revit instance.
The test runner is Microsoft.Testing.Platform; use `--project` as shown.
Release builds cover `Release.R22` through `Release.R26`.

Run Python tests and package builds on Windows, macOS or Linux with Python 3.11+ and uv:

```sh
cd server
uv run --with pytest pytest -q
uv build
```

Transport tests use mocked operations and a local fake HTTP server.
They do not require a Windows workstation.
Keep credentials and model files out of commits and use sanitized fixtures.

## Release assets

CI uploads installable R22 and R26 folder layouts after its tests pass.
A `v<version>` tag triggers all five add-in builds, Core/server tests and the Python wheel build.
The tag version must match `server/pyproject.toml`.
The release workflow attaches five ZIP files and the wheel to a GitHub Release with generated notes.
Extract each year's ZIP into `%APPDATA%\Autodesk\Revit\Addins\20<yy>` while that Revit instance is closed.
The archive root contains `RevitModelMcp.addin` and the `RevitModelMcp` assembly directory.
The workflow does not invoke the optional WiX installer pipeline.

Contributions are licensed under the [MIT license](LICENSE).
