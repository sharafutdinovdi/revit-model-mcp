---
description: "Understand Model MCP read-only controls, confirmation tokens, Undo limits, C# execution, and AI client privacy before connecting Revit."
---

# Is it safe? Read-only, Undo and privacy

Model MCP enables actions by default and provides a shared workstation read-only lock.
Committed transactional model edits create one named Revit Undo entry, but Undo does not reverse every file or external effect.
The selected AI client receives tool responses and may send them to its model provider.

## Start with a review

1. Enable read-only mode in the Model MCP ribbon or with `revit_set_read_only(enabled=true)`.
2. Check the lock state in `revit_ping` and inspect the target document before work.
3. Review the proposed action and code before confirming a change.
   Chat unlock requires explicit confirmation; machine policy cannot be lifted through chat or the ribbon.
4. Read the returned `summary`, activity entry, Undo name, and verification warnings.
5. Use Revit Undo or `revit_undo_last` only within its documented scope.
   The tool can undo the last action only while it remains the most recent Revit change.

Confirmation tokens are a handshake with the AI client, not proof of human approval.
Model MCP does not display its own confirmation dialog for that handshake.
C# execution runs with the Revit account's rights and can access files or the network.

## Check privacy and persistence

Model MCP has no publisher backend that collects model data.
The AI client and any configured remote transport determine where requested model responses go next.
Path redaction reduces exposed paths; model names and parameter values can remain visible.
Read-only mode protects model edits, while reports, exports, channel files, and logs can still write files.

## Further reading

- [Security boundaries](../security.md)
- [Privacy, data flow, and retention](../privacy.md)
- [Administrator security whitepaper](../admin/security-whitepaper.md)
- [Read-only mode and Undo](../actions.md#read-only-mode)
- [Policy reference](../admin/policy-reference.md)
