# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Revit 2027 build configuration and CI/release packaging.
- `install.ps1` with build/release sources, optional assembly signing and Claude Code registration.
- `dry_run` on every mutating action: the action executes inside a transaction that is always rolled back and returns the same verification block.
- `verification` on mutating actions with `before` and `after` facts re-read from the model after commit.
- `revit_batch`: up to 50 actions in one `TransactionGroup` assimilated into a single undo step, rolled back on the first failed step.

### Changed

- README quick start uses the install script.

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
