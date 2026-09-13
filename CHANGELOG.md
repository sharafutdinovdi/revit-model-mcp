# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Contributor Covenant 2.1 and repository instructions for coding agents.
- Release highlights from the tagged changelog section and direct installation links.

- Single-user and multi-user MSI release assets with CI extraction, installation and removal smoke tests.
- SHA256 checksums for the per-year ZIPs, MSIs, Python wheel and source distribution.
- PyPI trusted publishing and best-effort MCP Registry publishing through GitHub OIDC for stable releases.
- MCP Registry manifest and uvx configuration for Claude Code and Claude Desktop.
- WinGet manifest templates, generation, validation and optional submission with `WINGET_TOKEN`.

### Changed

- README presents installation and grouped tools; detailed actions, security and validation references live in `docs/`.
- Contributor guide includes test coverage and the release ritual.

### Fixed

- Python distributions include the MIT license file and advertise tested Python 3.13 support.

## [0.2.0] - 2026-09-12

### Added

- Revit 2027 build configuration and CI/release packaging.
- `install.ps1` with build/release sources, optional assembly signing and Claude Code registration.
- Default read-only coordinator tools: `revit_model_health`, `revit_links_status`, `revit_shared_coordinates` and `revit_parameter_fill_check`.
- `dry_run` on `revit_move`, `revit_place_family`, `revit_create_wall`, `revit_set_parameter` and `revit_delete`: a successful preview executes inside a transaction that is rolled back and returns the same verification block.
- `verification` with `before` captured before the change and `after` re-read after commit (or before rollback on a dry run).
- `verification.error` when the post-commit re-read fails.
- `revit_batch`: up to 50 actions in one `TransactionGroup` assimilated into a single undo step, rolled back on the first failed step.

### Changed

- README quick start uses the install script.
- MCP handshake version comes from package metadata.
- `install.ps1` reports a missing release asset for a Revit year with a clear message.

## [0.1.0] - 2026-09-11

### Added

- Windows Revit add-in with build configurations for Revit 2022-2026.
- Python MCP server with 14 default read tools for catalogs, queries, aggregation, parameters, warnings, relations, views and instance discovery.
- PNG view exports and optional element geometry in model millimetres.
- Eight opt-in action tools protected by a server flag and a workstation gate file.
- Local PowerShell, SSH file-channel and authenticated HTTP transports for remote workstations.
- Per-user HTTP token, loopback listener, path redaction and configurable channel directory.
- Core tests, server tests and CI artifacts for Revit 2022 and 2026.
- Tag-triggered release packaging for five Revit versions and a Python wheel.
- Installation, transport, protocol, security and contribution documentation.
