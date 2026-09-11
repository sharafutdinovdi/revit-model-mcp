# Contributing

Open an issue with the problem and expected behavior before a large change.
Keep pull requests focused.
Include relevant test results and the affected Revit versions.
Use English for code and public API descriptions.

Run C# builds and tests on Windows with the SDK selected by `global.json`.
Run Python tests with `cd server` followed by `uv run --with pytest pytest -q`.
See [testing](README.md#testing) for the build commands.
Use sanitized fixtures and keep credentials and model files out of commits.

Contributions are licensed under the [MIT license](LICENSE).
