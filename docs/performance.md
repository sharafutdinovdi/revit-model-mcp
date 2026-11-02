# Performance

The bench script times the MCP read and action paths against a live Revit workstation.
It lives in `build/bench/bench.py`.

## Run the bench

Run it on the Windows workstation where Revit and the add-in are running.
Open the model you want to measure and make it the active document.
From the repository root:

```powershell
$env:REVIT_MCP_HOST = 'local'
uv run --project server python build/bench/bench.py --runs 5 --label "v1.0.0" --out bench-results
```

The script starts the server with `uv run revit-model-mcp` in `server/`, calls the tools over stdio and prints a markdown table.
With `--out <dir>` it also writes `bench-<utc timestamp>.json` and `.md`.

| Option | Default | Meaning |
| --- | --- | --- |
| `--server-cmd` | `uv run revit-model-mcp` | Command that starts the server. |
| `--cwd` | `server` | Working directory of that command. |
| `--runs` | `5` | Measured runs per scenario. |
| `--document` | none | Document passed to every tool that accepts it. |
| `--scenarios` | all | Comma list of scenario names. |
| `--out` | none | Folder for the result files. |
| `--label` | none | Free text stored in the result, for example a commit or version. |

A failed scenario is recorded in the table and the run continues.
`actions_50` writes the `Comments` parameter on 50 elements and then calls `revit_undo_last`, so run it on a model you can change.
Leave it out with `--scenarios` on a model that must stay untouched.

## Scenarios

Each scenario reports the median, p95, minimum and maximum over its runs, the response size in bytes and `skippedCount` when the response carries it.

| Scenario | What it measures |
| --- | --- |
| `ping` | Round trip through the server and add-in with no Revit work. |
| `catalog`, `catalog_limited` | `revit_list_catalog` for categories with the maximum limit and with `limit` 50. |
| `aggregate_category`, `aggregate_category_type` | `revit_aggregate_elements` on the first catalog category, grouped by category, then by category and type. |
| `query_100`, `query_1000` | `revit_query_elements` pages of 100 and 1000 rows with up to 10 parameter fields from the catalog. |
| `details_50` | 50 sequential `revit_element_details` calls; the run time is the total, the notes give the per-call median. |
| `warnings` | `revit_list_warnings`. |
| `health` | `revit_model_health`. |
| `fill_check` | `revit_parameter_fill_check` with 3 parameters from the catalog. |
| `family_audit_compact`, `family_audit_full` | `revit_family_audit` on the first 10 project families, with `detail` set to `summary` (the compact level) and `full`. |
| `snapshot` | `revit_model_snapshot`. |
| `export_view` | `revit_export_view` of the first plan view to a PNG in a temporary folder. |
| `actions_50` | `revit_run_actions` with 50 `set_parameter` steps. The undo time is reported in the notes. |
| `cold_start` | Five fresh server starts, each timing `initialize` and `tools/list`. |

## Results

Numbers are added per release.

| Release | Revit | Workstation | Model | Scenario | Median ms | p95 ms |
| --- | --- | --- | --- | --- | --- | --- |
| | | | | | | |

Compare only runs from the same workstation and model.
