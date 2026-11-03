---
description: "Compare seven public Revit MCP servers using their own documentation: supported versions, installation, model access, and distinct strengths."
---

# Revit MCP servers compared

The best MCP server for Revit depends on the Revit version, required operations, deployment rules, and AI client.
Model MCP focuses on live coordination, per-change Undo, read-only controls, and MSI deployment across Revit 2022-2027.
Other projects offer vendor-maintained previews, broader application coverage, older Revit support, or browser connectors.

## Compare documented capabilities

Checked on **2026-11-03** against each project's own README or documentation linked in the table.
These are documented capabilities, not independent runtime benchmarks or security certifications.
A supported year does not establish that every operation has been tested on that year.

| Project and primary source | Revit versions stated | Access and installation | Distinct strength and trade-off |
| --- | --- | --- | --- |
| [Autodesk Revit MCP Server](https://help.autodesk.com/cloudhelp/ENU/ADSKMCP-RevitMcp/files/ADSKMCP_RevitMcp_revit_mcp_server_html.html) | 2027.2 Technical Preview | Separate Read, Write, and Experimental add-ons; local stdio | Autodesk-maintained tools and separate server choices. The documented preview targets 2027.2. |
| [mcp-servers-for-revit](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit#readme) | 2020-2026 | Read, create, modify, and delete; release ZIP, Node.js 18+, TypeScript server, and C# command sets | Documents support for older Revit years than Model MCP and an extensible command-set architecture. Requires Node.js. |
| [rvt-mcp by BIMwright](https://github.com/bimwright/rvt-mcp#readme) | 2022-2027 | Typed tools and C# execution; setup ZIP with self-contained server and add-ins; Apache-2.0 | Broader typed catalog and optional ToolBaker for personal tools. README states Family Editor authoring is out of scope. |
| [AEC Model Bridge](https://github.com/Sam-AEC/aec-model-bridge#readme) | 2024-2027 | Native add-in and Python hub; writes blocked until plan approval by default; GPL-3.0 plus commercial licensing | Broader cross-application scope with Rhino, Speckle, and IFC providers, plus a mock mode. README marks Navisworks work as in progress. |
| [RevitMCP by oakplank](https://github.com/oakplank/RevitMCP#readme) | Revit 2024 API compatibility is discussed; no complete year matrix in README | pyRevit extension, Routes, and Python server; separate web UI and stdio surfaces; MIT | Built-in browser workflow and detailed schedule operations suit pyRevit-based setups. Requires pyRevit and an external Python runtime. |
| [AECO MCPs Revit MCP](https://aecomcps.com/revit) | 2023-2027 | Add-in hosts the server; per-user installer; free to use; optional account and cloud access | Automatic client wiring, shared lessons, and an optional gateway for ChatGPT or claude.ai. Cloud access adds an external gateway to the route. |
| [Model MCP](../index.md) | 2022-2027 | Read and action tools; SingleUser or MultiUser MSI with bundled server; MIT | Coordination prompts, batch audit reports, native family authoring, one named Undo per committed transactional edit, and workstation read-only policy. Live validation coverage varies by client. |

## Choose and verify

1. Match the documented Revit year and deployment requirements first.
2. Check the required operation in the project's maintained tool reference.
3. Review authentication, code execution, approval, privacy, and rollback boundaries.
4. Install one candidate in a test model and verify reads before model edits.
5. Record the server release, client, Revit year, and any incomplete results.

The strengths column is a selection judgment from the linked documentation.
Tool counts change between releases and do not measure answer quality or model safety.
Model MCP does not provide the hosted browser connector offered by AECO MCPs or the multi-application hub described by AEC Model Bridge.

## Further reading

- [Model MCP tools](../tools.md)
- [Installation](../agent-install.md)
- [Tested clients](../tested-clients.md)
- [Security](../security.md)
