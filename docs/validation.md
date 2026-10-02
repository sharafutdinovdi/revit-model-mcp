# Validation evidence

Validation status as of 2026-10-01; ✅ denotes a completed check and - denotes no validation evidence for that check.
CI evidence includes the v0.1.0 release builds for R22-R26 and the R22/R26/R27 CI builds.
Local build evidence includes `Release.R26` and `Release.R27`, plus the Revit 2024 build through `install.ps1 -Source Build`.

| Revit year | Build in CI | Local build | Live reads | Live actions | Install script |
| --- | --- | --- | --- | --- | --- |
| 2022 | ✅ | - | - | - | - |
| 2023 | ✅ | - | - | - | - |
| 2024 | ✅ | ✅ | - | - | ✅ |
| 2025 | ✅ | - | - | - | - |
| 2026 | ✅ | ✅ | ✅ | ✅ | ✅ |
| 2027 | ✅ | ✅ | - | - | - |

Live checks use Revit 2026.5 build 26.5.0.55.
The September check used Autodesk's `Snowdon Towers Sample Architectural.rvt`, called from macOS over the SSH transport.
All 18 read tools, including the four coordinator tools, are live-validated.
Live action checks cover `select`, `show`, `isolate`, `move`, `create_wall`, `set_parameter`, `delete` and `batch`, with dry runs and real writes for actions that support them.
Installer checks cover `-Source Build` for 2024 and 2026 with `-SignThumbprint`, `-Source Release` for 2024 from v0.1.0, and `-Uninstall`.
Revit 2022 could not be live-validated in the October pass.
Screenshots and JSON evidence are on the [`validation-assets` branch](https://github.com/sharafutdinovdi/revit-model-mcp/tree/validation-assets).
The Revit undo menu label for a batch (`revit_batch`) cannot be verified through the API.

## Revit 2026 live validation

The October validation covered add-in builds `2db6698` through `7778d0c` on Revit 2026 build 26.5.0.55.
The consolidated record is tracked in [issue #23](https://github.com/sharafutdinovdi/revit-model-mcp/issues/23).

| Tool family | Result | Add-in build | Date |
| --- | --- | --- | --- |
| Interactive reads | Passed | `2db6698` | 2026-10-01 |
| Model snapshot and prompts | Passed | `2db6698` | 2026-10-01 |
| Safe actions | Passed | `2db6698` | 2026-10-01 |
| Batch collection | 11 completed; 2 stopped at error-severity dialogs that required a modifying answer | `7778d0c` | 2026-10-01 |
| Cancellation, worker crash recovery and running-instance isolation | Passed | `7778d0c` | 2026-10-01 |
| Report workbook and Changes sheet | Passed | `7778d0c` | 2026-10-01 |

### Batch collection

All source files remained unchanged.
Files saved in Revit 2022 through 2024 were opened in Revit 2026 and upgraded only in memory.

| Model | Size (MB) | Saved year | Result | Open (seconds) |
| --- | ---: | ---: | --- | ---: |
| M1 | 194 | 2022 | Completed | 1113 |
| M2 | 265 | 2022 | Failed at open: modifying answer required | - |
| M3 | 201 | 2022 | Completed | 727 |
| M4 | 155 | 2022 | Completed | 1055 |
| M5 | 155 | 2022 | Completed | 1050 |
| M6 | 206 | 2022 | Completed | 986 |
| M7 | 213 | 2022 | Completed | 987 |
| M8 | 50 | 2023 | Completed | 68.5 |
| M9 | 31 | 2024 | Completed | 54 |
| M10 | 20 | 2023 | Completed | 62 |
| M11 | 314 | 2022 | Failed at open: modifying answer required | - |
| M12 | 169 | 2022 | Completed | 1565 |
| M13 | 180 | 2022 | Completed | 1342 |

The worker peaked at about 7 GB of memory.
Completed 150-315 MB models opened in 12-26 minutes.
Error-severity dialogs stopped collection where the available continuation required `Disconnect` or `Delete`.
The unsigned add-in prompt must be accepted once per Revit year, followed by a normal Revit close to persist the choice.

## Batch checks

Core policy tests cover state transitions, cancellation and resume, year routing, deadlines, dialog decisions, and the read-only allowlist. Python boundary tests cover input validation, durable state visibility, selected PID, scheduled-task fallback, cancellation, fetch copy, and collisions. Windows CI restores the supervisor with a lock file and checks representative add-in builds, staged artifacts, and MSI contents for the supervisor executable and absence of Autodesk binaries. Live Revit scenarios remain tracked in issue #23; see [batch collection](batch.md).
