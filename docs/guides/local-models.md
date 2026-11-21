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
LM Studio has not been [tested with Model MCP](../tested-clients.md).
Set the client tool timeout above the tool's `timeout_seconds` (default 120 seconds).

### Jan 0.8.8 trial

A November 2026 trial ran Jan 0.8.8 and Ollama on a Mac against Revit 2026 on a Windows workstation over SSH, with Model MCP 2.1.0 and all 89 tools.

Setup the trial needed:

- Add Ollama as an OpenAI-compatible provider at `http://localhost:11434/v1` with any nonempty API key, then refresh the model list.
- Enable **Capabilities > Tools** for each model; it is off for newly found models.
- Set the context in Ollama, not in Jan: create a model tag from a `Modelfile` with `PARAMETER num_ctx 32768` and `PARAMETER temperature 0`, and confirm it with `ollama ps`. The OpenAI-compatible endpoint ignores a context size sent by the client.
- Add the stdio entry under **Settings > MCP Servers**; use the absolute path to `uvx` if Jan does not find it.
- Leave **Allow All MCP Tool Permissions** off and set **Tool call timeout** to 180 seconds.

Results with Qwen3 14B, each answer compared with a separate read from Revit:

| Question | Time to answer | Result |
| --- | ---: | --- |
| Which models are open? | 46 s | Correct project; wrongly said that no links were open |
| Group the warnings by type | 112 s | Correct total of 49; wrong split by type |
| How many doors have an empty Fire Rating, counting the type value? | 57 s | Correct: 29 |
| How many views are not placed on any sheet? | No answer in 217 s | The sheet list result filled the context |
| Delete one door, with the request denied in Jan | 74 s | Reported the denial; nothing was deleted |

The tool list used about 19,000 of 32,768 tokens with no truncation.
Qwen3 8B gave no usable answer in this trial.
A call with `document: "active"` failed; name the model in the question.
