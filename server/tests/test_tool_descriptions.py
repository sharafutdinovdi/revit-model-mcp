from __future__ import annotations

import asyncio
import re

from revit_model_mcp import server as revit_server

IMPERATIVE = re.compile(
    r"(^|[.;:]\s+|\n)(Call|Use|Show|Prefer|Always|Never|Do not|Pass|Supply|Start with|Retry|Poll)\b"
)
GATED_PHRASE = re.compile(r"only after|chat approval|before running", re.IGNORECASE)


def _texts() -> list[tuple[str, str]]:
    texts = [("instructions", revit_server.mcp.instructions or "")]
    for tool in asyncio.run(revit_server.mcp.list_tools()):
        texts.append((f"{tool.name} description", tool.description or ""))

        def walk(node, path):
            if isinstance(node, dict):
                description = node.get("description")
                if isinstance(description, str):
                    texts.append((f"{tool.name} {path}", description))
                for key, value in node.items():
                    walk(value, f"{path}.{key}")
            elif isinstance(node, list):
                for index, value in enumerate(node):
                    walk(value, f"{path}[{index}]")

        walk(tool.input_schema, "input")
    return texts


def test_descriptions_state_behavior_without_directing_the_model():
    offenders = [
        f"{where}: {match.group(0).strip()!r}"
        for where, text in _texts()
        for match in [IMPERATIVE.search(text) or GATED_PHRASE.search(text)]
        if match
    ]
    assert not offenders, "Model-directed wording in descriptions:\n" + "\n".join(offenders)
