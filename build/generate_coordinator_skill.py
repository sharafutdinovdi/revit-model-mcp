"""Render the coordinator skill from the packaged guide."""

from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GUIDE = ROOT / "server" / "revit_model_mcp" / "guides" / "coordinator.md"
SKILL = ROOT / "skills" / "revit-model-coordinator" / "SKILL.md"
FRONTMATTER = """---
name: revit-model-coordinator
description: Use for Revit model review, coordination, or model audit with the revit-model MCP server.
---

## Use this skill

Read the guide below before reviewing a model through the MCP server.

"""


def render_skill(guide: str) -> str:
    return FRONTMATTER + guide.rstrip("\n") + "\n"


def main() -> None:
    SKILL.parent.mkdir(parents=True, exist_ok=True)
    SKILL.write_text(render_skill(GUIDE.read_text(encoding="utf-8")), encoding="utf-8")


if __name__ == "__main__":
    main()
