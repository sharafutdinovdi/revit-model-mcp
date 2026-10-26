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
The `parameters` field in workstation responses contains JSON schema text.

`revit_run_tool(name, args, dry_run, confirm_token)` validates arguments and verifies approval before compilation or execution.
Action mode uses the same confirmation flow as `revit_execute_code`: a first real call runs nothing and returns a token, and a confirmed retry runs the identical revision and arguments.
A dry run executes and rolls back model changes without a token.
Read mode always rolls back the model transaction and requires no run token.
Both modes require an open target document and use `document` for addressing.
The script cannot own the transaction.
A committed model change has one undo entry named `Model MCP: <tool name>`, an Activity record, and a summary.
A procedure that changes nothing leaves no undo entry.
Long calls can return `status:running`; polling `revit_jobs` retrieves the original result.
A timeout or failed verification can follow a committed change.
Resubmitting can repeat that change.

## Ribbon and deletion

The **My tools** ribbon button opens without a document.
The static window has no animation and uses 0 ms transitions.
Its list shows name, description, mode, short revision, tested Revit years, and updated date.
**Show code** opens a read-only source and parameter viewer.
**Delete** uses a Revit confirmation dialog with No selected by default.
Tools run through the MCP client; the ribbon has no run command.
An empty list explains how to save the first tool.
Load errors provide a message and a recovery action.

`revit_delete_tool(name)` returns a confirmation challenge.
A confirmed repeat deletes the tool and all its revisions.
Deletion cannot be undone in Revit.

## Storage and security

Revisions live under `%LOCALAPPDATA%\RevitModelMcp\tools\<name>\<revision>.json` for the current Windows user.
The `active` file selects one revision.
Writes use temporary files, flush to disk, and atomically replace each destination.
A store lock serializes saves and deletions across Revit processes.
New revisions retain earlier source files.
The revision hash covers source normalized to LF line endings with outer whitespace removed and one final newline.
Approval also covers metadata and schema with a store-local HMAC signature.
A changed source, signature, schema, mode, or pointer causes `revision not approved` at run time.
Copying revision files from another store does not approve them; saving through the confirmation flow is required.
The local approval key remains in the per-user tools directory.

Saved C# has full Revit API access and the Windows user's file and network rights.
Neither read mode nor dry run is a sandbox for file or network operations.
The approval files defend against accidental edits and unapproved imports, not an attacker who controls the Windows account and its approval key.
The workstation read-only lock blocks saving, deleting, and running tools, including read-mode and dry-run execution.
List and review remain available while locked.
All saved tool commands inherit `ExecuteCode`, channel defaults, and restrictions on `execute-code` in addition to their own tool policies.
An administrator's policy cannot be lifted through chat or ribbon.
Source and response path redaction follow the existing execution pipeline.
Saved source itself remains local user data and must not contain credentials.

## Verification

Core tests cover normalized hashes, argument validation, approval binding, tamper refusal, persistence, atomic file cleanup, and catalog limits.
Python tests cover schemas, confirmation forwarding, annotations, and policy refusals.
Live verification happens on a Revit workstation: compile-only save, typed parameters, action confirmation, one named Undo entry, Activity, summaries, rollback, lock enforcement, zero-document ribbon availability, keyboard focus, and both icon themes.
