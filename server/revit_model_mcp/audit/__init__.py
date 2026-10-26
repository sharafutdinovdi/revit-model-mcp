"""Model audit and issue register interoperability."""

import json


def to_register_issues(result):
    """Convert failed findings into issue register inputs without changing the result."""
    issues = []
    for finding in result["findings"]:
        if finding["status"] != "failed" and not (
            finding["status"] == "partial" and finding.get("violated")
        ):
            continue
        reference = finding.get("reference", {})
        issues.append(
            {
                "title": finding["title"],
                "category": finding.get("pack") or "model-audit",
                "finding": json.dumps(
                    {
                        "measured": finding["measured"],
                        "threshold": finding["threshold"],
                        "population": finding["population"],
                    },
                    ensure_ascii=False,
                ),
                "severity": finding["severity"],
                "requirement_source": reference.get("source", ""),
                "requirement": ": ".join(
                    reference[k] for k in ("clause", "text") if reference.get(k)
                ),
            }
        )
    return issues
