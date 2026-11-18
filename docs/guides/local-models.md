---
description: "Experimental local-model setup, first-tool-call measurements, client support and safety limits for Model MCP."
---

# Use Model MCP with a local model

**Experimental.** Local models can select valid tools with incorrect arguments or report actions that did not happen.
The measurements below cover first-tool selection, not completed work in Revit.

!!! warning "Verify actions in Revit"
    In a simulated deletion-confirmation test, Qwen3 14B claimed that deletion was confirmed and done in all three repeats.
    It neither requested user approval nor called the tool again.
    Qwen3 8B requested user approval each time.
    No deletion was executed.

    Keep per-call approval enabled in the client; never allow all tools globally.
    Check results in Revit, not just the model's reply.
    Enable [workstation read-only mode](../actions.md#read-only-mode) for exploration.
    Model MCP's own confirmation step still blocks unconfirmed large deletions, but cannot stop a model from describing an action that did not happen.
    Confirmation tokens are not proof of human approval; the client must present the request to the user.

## Setup

Run a local MCP client and a local model on your machine.
The Model MCP server runs next to the client as usual and connects to Revit locally or over the [SSH transport](../transport.md).
No special Model MCP configuration is needed; use the full tool set without a preset.

After [installing the add-in and uv](../install.md), use this stdio MCP entry on the Revit workstation:

```json
{
  "mcpServers": {
    "revit-model-mcp": {
      "command": "uvx",
      "args": ["revit-model-mcp"],
      "env": {
        "REVIT_MCP_HOST": "local",
        "REVIT_MCP_REDACT_PATHS": "1"
      }
    }
  }
}
```

For a client on another machine, configure the SSH transport instead of `local`.

## Measured first calls

A November 2026 measurement used one Mac with Apple M5 Pro and 24 GB memory, Ollama, 32k context, temperature 0 and all 89 tools.
Each of 15 typical coordinator questions was repeated three times.
Only the first tool call was scored; no Revit connection or completed operation was tested.

| Model | Fully intended first call | Schema-valid calls | Mean first-call latency | Sampled peak Ollama process memory |
| --- | ---: | ---: | ---: | ---: |
| Qwen3 14B | 15/45 | 45/45 | 3.5 s | About 12 GB |
| Qwen3 8B | 12/45 | 45/45 | 2.4 s | About 8.6 GB |

Valid schemas do not establish correct arguments.
Tool descriptions and server instructions occupied about 19,000 input tokens; 16k context is too small.
Long results and chat history need additional context.
The sampled memory peaks are observations, not guaranteed ceilings.

These tasks produced the fully intended first call in all three repeats:

- **Both models:** list open models; group warnings by type; list sheets and unplaced views.
- **Qwen3 14B also:** find doors without a fire rating value; find model views not placed on any sheet.
- **Qwen3 8B also:** list worksets.

## Clients

[LM Studio](https://lmstudio.ai/docs/app/mcp) and [Jan](https://docs.jan.ai/desktop/mcp) document stdio MCP support and per-call approval.
Neither has been [tested with Model MCP](../tested-clients.md).
Set the client tool timeout above the tool's `timeout_seconds` (default 120 seconds).
