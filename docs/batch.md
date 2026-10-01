# Batch model collection

Batch collection runs on a Windows Revit workstation.
It starts a separate Revit worker, opens one model at a time in the background, reads the opened document through the worker-only `batch-snapshot` command, and closes the model without saving.
The existing snapshot reader supplies schema version 1 JSON.
Batch collection does not save, synchronize, start transactions, or expose action commands to the worker.

## Before the first batch run

For each installed Revit year, start Revit once and verify that any unsigned add-in trust dialog names Revit Model MCP and `RevitModelMcp.dll`.
Choose Always Load, then close Revit normally before starting a batch run.
Revit remembers Always Load for the same add-in location and AddInId; replacing identical DLL contents or changing only the file timestamp does not require trust again.
If a batch worker encounters this dialog, startup fails with the affected year and the worker is stopped.
If a worker reaches its startup deadline before any heartbeat, the supervisor skips worker startup for later models that need the same Revit year.
Those models fail immediately with the original startup failure.
Other Revit years continue in the same run.

## Tools and inputs

`revit_batch_start(paths=null, folder=null, recursive=false, parameter_rules=null, years=null, open_timeout_minutes=null)` accepts exactly one source.
`paths` is a nonempty list of absolute `.rvt` or `.rfa` local, UNC, or RSN paths.
A `folder` discovers those extensions at its top level unless `recursive=true`.
Blank entries, duplicate normalized paths, unsupported extensions, and both or neither source fail before a run is created.
`years` is a distinct list of supported integers from 2022 through 2027 and limits the installed Revit versions available for routing.
`parameter_rules` is a list of nonblank `{category, parameter}` objects passed to `batch-snapshot`.
The per-model open deadline is 30 minutes, or 45 minutes when the model is upgraded in memory from an older Revit year.
An in-memory upgrade can take many minutes during open.
`open_timeout_minutes` accepts an integer from 5 through 180 and overrides either default for the run.
An explicit value is persisted in `run.json` as `openTimeoutMinutes`.
Startup, pre-pass, snapshot, and close deadlines are fixed.

Start returns a `runId` and accepted model count.
The workstation stores immutable inputs and mutable state in `ROOT\runs\<runId>\run.json`.
Workstation batch JSON inputs, including the dialog allowlist, accept UTF-8 with or without a BOM.
The supervisor replaces this file atomically.
Its lifetime is independent of the MCP client.
`revit_batch_status(run_id)` reads persisted progress and marks unfinished models failed when the recorded supervisor process has exited.
Completed and failed models stay terminal on restart.
An interrupted running model returns to pending when the supervisor restarts.
`revit_batch_cancel(run_id)` writes a durable cancellation marker, stops new work, closes an opened model without saving when possible, and marks remaining models cancelled.
`revit_batch_fetch(run_id, dest_dir)` accepts completed, failed, and cancelled runs.
It downloads snapshots from completed models to new client files.
An active run is refused with the completed and total model counts.
A completed model with a missing or invalid snapshot name is an error.
Local files are never overwritten, including when a destination appears during download.
The response has `runId`, `localPaths`, and `models`.
Each entry in `models` has `path`, `status`, and `localPath`.
`localPath` is null when no snapshot was downloaded.
An existing model `error` or `reason` is included when present.
Dialog diagnostics appear in `revit_batch_status` and `revit_batch_fetch` when present.
Each dialog record includes its ID, runtime type, available message capped at 2000 characters, available buttons, decision, result, model path, phase, and UTC time.
Path redaction applies to nested dialog paths and paths in messages and button captions without changing workstation state.
Fetched snapshot paths follow `REVIT_MCP_REDACT_PATHS` without changing workstation snapshots.
Fetch uses the existing SSH artifact transfer path for a remote workstation.

Each model can fail at worker startup, metadata pre-pass, open, snapshot collection, or close. A deadline, stale heartbeat, worker exit, or unknown modal dialog fails that model and recycles only the supervisor-owned worker. The next model continues. A timeout after an operation may have occurred is reported as an error; inspect persisted state before retrying.
The supervisor briefly retries heartbeat reads when the file is temporarily missing or inaccessible.

## Dialog allowlist

The workstation reads `%LOCALAPPDATA%\RevitModelMcp\batch-dialogs.json` before each model open, snapshot, and close phase.
A missing file leaves the built-in allowlist empty.
The file contains a JSON array:

```json
[
  {
    "dialogId": "TaskDialog_Example",
    "type": "TaskDialogShowingEventArgs",
    "result": 1
  }
]
```

An entry matches only the exact dialog ID and runtime event args type.
An invalid or unreadable file fails the current phase before model work. During open, the model is not opened.
An unknown dialog is never overridden and fails the model.
Allow only dialogs whose selected answer does not modify or save the model.

### Reading a dialog record and choosing a result

Read `dialogId` and `type` as the exact match keys. `message` contains up to 2000 characters of available dialog text. Each entry in `buttons` has a `caption` and a numeric `result`. `decision` shows whether the dialog was allowed or unknown. `phase` identifies the batch operation, and `modelPath` identifies the affected model.

For task dialogs, `result` is the task dialog result value, such as 1 for OK, 2 for Cancel, 6 for Yes, 7 for No, or 1001 and up for command links. For other dialogs, use a button's `result` from the record. Choose only an answer that does not modify or save the model.

For example, this record offers a Cancel result:

```json
{
  "dialogId": "Dialog_Revit_DocWarnDialog",
  "type": "DialogBoxShowingEventArgs",
  "message": "The document requires review.",
  "buttons": [{"caption": "Cancel", "result": 2}],
  "decision": "unknown",
  "result": null,
  "modelPath": "Example.rvt",
  "phase": "open",
  "timeUtc": "2026-09-30T00:00:00Z"
}
```

The matching allowlist entry uses the exact `dialogId` and `type` and the chosen button result:

```json
{"dialogId": "Dialog_Revit_DocWarnDialog", "type": "DialogBoxShowingEventArgs", "result": 2}
```

Message and buttons are read from the displayed dialog window on a best effort basis. Custom drawn dialogs may expose no text or buttons. Allowlisted dialogs are answered without display, so only a message provided by Revit in the event can be recorded.

## Year routing and sources

For local and UNC files, a worker reads `BasicFileInfo.Format` before open. For RSN, the configured Revit Server REST `/contents` response supplies `ProductVersion`; `/modelInfo` and `/history` supply activity metadata. The saved-year source is recorded separately from activity provenance. Set `REVIT_MCP_RSN_REST_BASE` to the matching `AdminRESTService.svc/` URL. The configured host must match the RSN path. Windows credentials used by the supervisor must have access. A missing endpoint, authentication failure, or unsupported response fails that model's pre-pass; the supervisor does not guess credentials or endpoints. Cloud model paths are unsupported.

Routing prefers the exact installed saved year. Otherwise it chooses the nearest allowed installed newer year and records `upgradedInMemory=true`. It refuses a saved year newer than all allowed installed versions. The saved year is never rewritten by routing. The detached document is never saved.

The workstation can run multiple Revit processes. This uses memory and may consume another Revit license or seat. Check local licensing and available resources before starting large runs.

## Interactive task for SSH

An SSH service session cannot launch a usable GUI Revit worker. Configure a Windows Scheduled Task named by `REVIT_MCP_ACTIVATE_TASK` to run interactively as the same signed-in user as the add-in. Use the task's **Run only when user is logged on** setting and `/IT` when creating it. Its action should start or restore the user's Revit session and leave the add-in available. The server invokes `schtasks /Run /TN <task-name>` when no suitable Revit instance is available, then discovers the instance again. `/IT` requires the user to be logged in. Behavior while the desktop is locked, an RDP session is disconnected, or the user is logged off must be tested on the target workstation; it is not guaranteed by this protocol.

## Explicit process addressing

Read and document lifecycle tools accept optional positive integer `process_id` (alias `processId`). An explicit PID takes precedence over document selection. If both are supplied, the document must match that instance. The selected heartbeat identity is checked again before job publication. Omitted PID keeps the existing single-instance and document selection rules. Use `revit_list_instances` to discover current PIDs; a stale or replaced PID fails before submission.
