---
description: "Save, inspect, and reuse reviewed Model MCP C# procedures as personal tools in Revit."
---

# My tools

My tools saves an approved C# procedure with parameters on the Revit workstation.
The MCP catalog stays fixed: saved procedures run through `revit_run_tool`, without dynamic tool registration.
The `code` toolset contains the saved tool commands.

## Save a procedure

After a successful multi-step or C# procedure, the agent offers once to save it as a parameterized tool.
Saving requires the user's explicit yes.
The agent chooses a name matching `^[a-z][a-z0-9_]{2,47}$`, a description, a parameter schema, and mode `read` or `action`.
The optional `tested_revit_years` records years actually verified by the author, from 2022 through 2027.
An empty list claims no tested years.

`revit_save_tool` compiles the source and validates the schema without running the script.
The first call returns `data.needsConfirmation`, `data.confirmToken`, a summary, and `data.tool` with the full source and SHA-256 revision.
The agent shows the source, parameters, mode, and confirmation text to the user.
Only after explicit approval does it repeat the identical call with `confirm_token`.
Tokens are single use, expire after five minutes, and bind the tool name, source revision, description, parameter schema, mode, and tested years.
A changed definition requires a new review.

The source accepts the same method body or public static `Script.Execute(ScriptContext ctx)` compilation unit as `revit_execute_code`.
Parameters are available through `ctx.Args` as `string`, `long`, `double`, and `bool` values.

```json
{
  "name": "count_walls",
  "description": "Count walls whose name contains a supplied label.",
  "parameters": {
    "type": "object",
    "properties": {
      "label": { "type": "string", "maxLength": 100 }
    },
    "required": ["label"],
    "additionalProperties": false
  },
  "code": "var label = (string)ctx.Args[\"label\"]; return new FilteredElementCollector(ctx.Document).OfClass(typeof(Wall)).Count(wall => wall.Name.Contains(label));",
  "mode": "read",
  "tested_revit_years": []
}
```

The schema supports an object with primitive `string`, `number`, `integer`, and `boolean` properties.
Property rules support `description`, `enum`, `minimum`, `maximum`, and `maxLength` where applicable.
The root supports `required` and `additionalProperties:false`.
Nested objects, arrays, references, patterns, defaults, and other schema keywords are rejected.
Unknown arguments, missing required arguments, type mismatches, and violated bounds report the parameter name.
Integer arguments must fit `Int64`.
Schemas and argument objects are limited to 64 KB UTF-8 each.
Source is limited to 256 KB UTF-8, and the active catalog holds at most 200 tools.

## Review and run

`revit_list_tools` lists names, descriptions, parameters, modes, revisions, tested years, authors, and revision dates without source.
`revit_get_tool(name)` returns the full active revision for review.
The list returns valid entries in `tools` and damaged folders in `invalid`, with a name and generic reason.
A damaged folder does not prevent listing or confirmed deletion, and cannot run.
The `parameters` field in workstation responses contains JSON schema text.

`revit_run_tool(name, args, dry_run, confirm_token)` validates arguments and verifies approval before compilation or execution.
Action mode uses the same confirmation flow as `revit_execute_code`: a first real call runs nothing and returns a token, and a confirmed retry runs the identical revision and arguments.
The first run of every name and revision hash in each Revit process requires a `confirm_token`, including read mode and dry runs.
Execution tokens bind `dry_run` as well as the code, transaction mode, tool revision, approval, and arguments.
A preview token cannot authorize a commit, and a commit token cannot authorize a preview.
A supplied token is always checked and consumed, even after the first confirmed run.
The confirmation shows the tool name, mode, short revision hash, saved timestamp, replaced revision hash when present, and validated arguments truncated to 300 characters.
After a confirmed run, read mode and dry runs require no further token for that revision in that session.
Action runs that commit changes still require confirmation for each call.
Read mode and dry runs roll back model changes.
Both modes require an open target document and use `document` for addressing.
`transaction="auto"` owns the transaction; the script must not start a Transaction.
`transaction="group"` opens no outer Transaction and permits script-owned transactions and native edit scopes.
Every script-owned scope must finish before return; errors and unfinished scopes roll back the group.
See [transaction ownership](actions.md#execute-c-code) for the shared runner and workstation validation requirements.
A committed model change has one undo entry named `Model MCP: <tool name>`, an Activity record, and a summary.
A procedure that changes nothing leaves no undo entry.
Long calls can return `status:running`; polling `revit_jobs` retrieves the original result.
A timeout or failed verification can follow a committed change.
Resubmitting can repeat that change.

## Ribbon and Settings

**My tools** is a pulldown on the Model MCP panel.
Its first 24 visible approved tools have individual buttons with labels, icons, descriptions, modes, short revisions and tested years.
Buttons are ordered by presentation order, then by the MCP tool name.
Unused slots are hidden; overflow tools remain runnable in **Settings**.
The pulldown refreshes at startup, after a save or deletion through MCP in this process, and after Settings closes.
Each refresh reads the shared per-user store, including changes made by another Revit process.
**Settings** remains available without a document; tool buttons require an active document.

The first local run of a name and revision in each Revit process opens a review dialog with its mode, revision, saved timestamp, replaced revision and tested years.
An untested running year, including an empty tested-years list, produces a warning.
The ribbon review set is separate from the MCP token-confirmed set.
A local approval never waives an MCP token, and an MCP token never waives the local review.

Tools with parameters open a schema-generated form with required markers, descriptions and typed controls.
Validation uses the same Core validator as `revit_run_tool` and identifies the parameter in errors.
The form remembers the last validated arguments for the current Windows user.
Transaction mode is stored per tool with default `auto`; **Transaction** in Settings toggles `auto` and `group`.
The parameter form preselects the stored mode and remembers a changed selection on execution.
Tools without parameters also use the stored mode, including `group` for script-owned transactions.
Action tools offer **Preview**, which rolls back Revit model changes and displays the result, and **Run (change model)**.
Tools without parameters skip the form; action tools require a separate model-change confirmation.
Read tools run with rollback.
Execution displays a wait cursor and a status window.
The result dialog shows success or failure, a summary and returned text, truncated for display with **Copy result**.

Settings lists names, descriptions, modes, short revisions, tested years, saved timestamps and replacements.
**Run**, **Show code**, **Rename**, **Icon** and **Hide from ribbon** apply to approved tools.
**Move up** and **Move down** change presentation order and are reachable by keyboard.
Rename changes only the display label; the MCP name remains the identifier.
Icons come from the built-in set or a decoded, single-frame PNG of at most 256 KB and 1024 pixels per dimension.
Imported icons are resized into 16 px and 32 px copies.
Stored and imported PNG dimensions are checked before full pixel decoding.
Invalid rows show `not approved`, cannot run, and allow confirmed deletion.
**Show code** opens a read-only source and parameter viewer.
**Delete** requires confirmation with No selected by default.
The windows have no animation.
An empty list explains how to save the first tool; load errors provide a recovery action.

`revit_delete_tool(name)` returns a confirmation challenge.
A confirmed repeat deletes the tool and all its revisions.
Deletion cannot be undone in Revit.

## Storage and security

Revisions live under `%LOCALAPPDATA%\RevitModelMcp\tools\<name>\<revision>.json` for the current Windows user.
The `active` file selects one revision.
Writes use temporary files, flush to disk, and atomically replace each destination.
A store lock serializes saves and deletions across Revit processes.
Each tool retains at most 20 revisions.
Saves delete the oldest non-active revisions above that limit.
Lock acquisition retries for up to two seconds and then reports `My tools store is busy` without a path.
The revision hash covers source normalized to LF line endings with outer whitespace removed and one final newline.
Approval also covers metadata and schema with a store-local HMAC signature.
A changed source, signature, schema, mode, or pointer causes `revision not approved` at run time.
Copying revision files from another store does not approve them; saving through the confirmation flow is required.
The local approval key remains in the per-user tools directory.

Presentation lives separately under `%LOCALAPPDATA%\RevitModelMcp\tool-presentation\<name>.json`.
It contains the display label, icon choice and resized PNG copies, hidden flag, sort order, transaction mode and last arguments.
The default label replaces underscores with spaces and capitalizes the first letter.
The default icon is selected deterministically from the built-in set by tool name.
Presentation never changes an approved revision, its hash or its signature.
Missing or corrupt presentation files use defaults and do not damage the approved catalog.
Deleting a tool also attempts to delete its presentation file.
A missing presentation file or folder is harmless; a locked or read-only file leaves a neutral cleanup note without changing the successful tool-deletion result.
The ribbon refreshes after deletion even when presentation cleanup cannot finish.
Labels and last used arguments are stored unencrypted in the Windows user's profile.
Secrets do not belong in tool arguments or display labels.

Saved C# has full Revit API access and the Windows user's file and network rights.
Neither read mode nor dry run is a sandbox for file or network operations.
The approval files defend against accidental edits and unapproved imports, not an attacker who controls the Windows account and its approval key.
The workstation read-only lock blocks saving, deleting, and running tools, including read-mode and dry-run execution, just as it blocks `revit_execute_code` with `dry_run=true`.
List and review remain available while locked.
All saved tool commands inherit `ExecuteCode`, channel defaults, and restrictions on `execute-code` in addition to their own tool policies.
An administrator's policy cannot be lifted through chat or ribbon.
Response path redaction preserves tool source, parameters, and description in save and get previews.
Model paths elsewhere in responses remain redacted.
Tool run audit lines record name, revision, source (`ribbon` or `mcp`) and argument names without argument values.
Validation rejects Unicode Format characters in source, names, descriptions, and parameter names.
Saved source itself remains local user data and must not contain credentials.

## My tools threat model

Approval protects against silent tool replacement between sessions by requiring a first-run confirmation in every Revit session.
The confirmed set exists only in Revit process memory, keyed by tool name and revision hash.
Saving a revision with a valid HMAC does not add it to that set.
The approval key is readable by code executing as the Windows user.
Approval does not protect against a client that can already execute code in the current session.
`execute_code` dry-run semantics remain unchanged; rollback covers Revit model changes and does not sandbox file or network access.
Internal `ToolStore` and `ToolDefinition` types prevent direct script calls through the Core assembly surface as defense in depth, not as a security guarantee.
My tools use the same machine policy, workstation read-only lock, transport authentication, and execution gates as `execute_code`.

A click in Revit is a local human action, not an MCP token exchange.
It enters the same `run-tool` execution pipeline with approval/HMAC, active revision, argument validation, machine policy, store-channel rules, workstation lock, transaction ownership, named Undo and Activity gates.
The revision is checked again after the local dialogs; replacing it during review refuses execution.
The first-run review protects against unnoticed revision changes and untested Revit years.
It does not prove that the source is safe or limit its capabilities.
Saved C# retains full Revit API access and the Windows user's file, network and process rights.

## Verification

Core tests cover normalized hashes, argument validation, approval binding, tamper refusal, persistence, atomic file cleanup, catalog limits, presentation defaults and corruption, slot assignment, form validation, independent session confirmations and refusal mapping.
Python tests cover schemas, confirmation forwarding, annotations, and policy refusals.
Run the following workstation checklist in Revit 2022 and 2026 at minimum; other supported years also require workstation verification.

1. Check all 24 slot commands, hidden and overflow tools, zero-document Settings, and refresh after MCP save/delete, Settings close and changes in another Revit process.
2. Check first-use revision review, tested-year warnings, independent MCP tokens and ribbon review, and confirmation of every action run.
3. Check typed fields, named validation errors, remembered arguments, Preview rollback, and stored `auto`/`group` modes both with and without a parameter form.
4. Check workstation lock, `ExecuteCode=off`, `ToolDeny` for `run-tool`, ToolAllow and the store-channel administrator rule on both paths.
5. Check rename, Move up/down, hide/show, default and custom icons, oversized or corrupt stored/imported PNG rejection before pixel decoding, keyboard order, both themes and no animation.
6. Delete through MCP and Settings without prior presentation data, then with locked or read-only presentation data; verify successful deletion, a neutral note without local paths, and immediate ribbon refresh.
7. Check named Activity rows, one named Undo entry, audit source without argument values, result copying and truncation, and wait feedback during long execution.
