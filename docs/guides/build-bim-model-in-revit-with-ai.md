---
description: "Plan native Revit family authoring, stairs and railings, linked structure checks, and project naming with AI and Model MCP."
---

# Build a BIM model in Revit with AI

Model MCP can create and edit native Revit elements through action tools and reviewed C# execution.
Native family authoring, stairs, and railings require Revit API constraints, verified dimensions, and deliberate transaction boundaries.
The workflow starts with agreed requirements and ends with model and geometry checks.

## Plan the model

1. Establish the Revit year, target document, units, levels, grids, templates, and required family types.
2. Inspect existing content with `revit_list_catalog`, `revit_query_elements`, and `revit_element_details`.
3. Agree the geometry, hosting, materials, parameters, and naming rules before creation.
4. Keep the structural model linked where the agreed delivery workflow requires it.
   Read link status and transforms with `revit_links_status` and `revit_shared_coordinates`, then compare levels and grids with `revit_compare_link_datums`.
5. Review each proposed edit, preserve the workstation lock and policy, and verify each committed result.

## Author native families

Follow the public [Revit family authoring skill](../../skills/revit-family-authoring/SKILL.md).
Create a family document from the agreed template with `revit_new_document`.
Use `revit_api_lookup` for signatures in the running Revit year and small `revit_execute_code(transaction="auto")` steps for family geometry and constraints.
The server owns the transaction in `auto` mode.

Flex family types and dimensions, verify hosting and connectors where applicable, and retain family-audit diagnostics.
Save the RFA with `revit_save_document`, then load it separately with `revit_load_family` and test placement with `revit_place_families`.
Cross-document save and load are separate operations.

## Create stairs and railings

Stairs use native edit scopes such as `StairsEditScope`.
Use `revit_execute_code(transaction="group", dry_run=true)` for a reviewed preview when the script needs its own transactions and edit scopes.
The group runner has no outer transaction; every script-owned transaction and scope must finish before return.
Successful edits are assimilated into one named Undo entry; preview and failures roll back the group.

After reviewing the preview, submit the same script without the dry run to request a confirmation token.
After explicit confirmation, repeat that request with the token.
Check stair levels, riser counts, runs, landings, railing types, hosting, and geometry in Revit.
Railings may require a separate reviewed code step for the chosen stair or path.
There is no dedicated stair or railing tool in the typed catalog.
Use the maintained [grouped transaction example](../actions.md#execute-c-code) for the contract.

## Apply project naming rules

ISO 19650 does not supply one universal family, type, or sheet naming pattern.
Use the project's agreed information requirements, naming convention, classification, and parameter rules.
Check existing values before proposing renames or parameter updates.
The [ISO 19650 issue register skill](../../skills/iso19650-issue-register/SKILL.md) can record deviations with requirement references and evidence.

## Verify and record

Read back created IDs, parameter values, and relationships, then inspect model views or exported images.
Retain `summary`, Undo names, and timeout or verification warnings even when an edit committed.
Saving, exporting, and synchronizing have separate confirmation and persistence boundaries.

## Further reading

- [Actions and transaction modes](../actions.md)
- [AI model coordination](ai-model-coordination-in-revit.md)
- [Public workflow skills](../prompts.md#workflow-skills)
- [Safety](safety.md)
