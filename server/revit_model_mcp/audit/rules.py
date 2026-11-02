"""Load and validate declarative audit rules before reading a model."""

import hashlib
import json
from copy import deepcopy
from pathlib import Path

import yaml
from jsonschema import Draft202012Validator, FormatChecker

ROOT = Path(__file__).parent
VALIDATOR = Draft202012Validator(
    json.loads((ROOT / "rule-schema.json").read_text()), format_checker=FormatChecker()
)
PACKS = ("model-health", "coordination", "sheets-and-views", "iso19650-information")


def load_rules(path=None, inline=None):
    """Resolve builtin inheritance and return validated rules with provenance."""
    if path is not None and inline is not None:
        raise ValueError("Use rules or rules_inline, not both.")
    label = path or ("inline" if inline is not None else "pack:model-health")
    packs = []

    def resolve(value, stack=()):
        VALIDATOR.validate(value)
        merged = {}
        for pack in value.get("extends", []):
            if pack not in PACKS or pack in stack:
                raise ValueError(f"Unknown or cyclic pack: {pack}")
            packs.append(pack)
            inherited = resolve(
                json.loads((ROOT / "packs" / f"{pack}.json").read_text()), (*stack, pack)
            )
            for inherited_rule in inherited:
                key = inherited_rule["id"]
                if key in merged and merged[key] != inherited_rule:
                    raise ValueError(f"Conflicting inherited rule: {key}")
                merged[key] = inherited_rule
        seen = set()
        for rule in value["rules"]:
            key = rule["id"]
            if key in seen:
                raise ValueError(f"Duplicate rule id: {key}")
            seen.add(key)
            if key in merged:
                for field in rule.keys() - {"id", "severity", "threshold", "enabled"}:
                    if rule[field] != merged[key].get(field):
                        raise ValueError(f"Override cannot change {field}: {key}")
                merged[key].update(rule)
            else:
                if not {"title", "severity", "check"} <= rule.keys():
                    raise ValueError(f"Incomplete rule: {key}")
                merged[key] = deepcopy(rule)
                merged[key]["pack"] = stack[-1] if stack else None
        if len(merged) > 200:
            raise ValueError("At most 200 resolved rules are allowed.")
        return list(merged.values())

    if inline is not None:
        value = deepcopy(inline)
    elif label.startswith("pack:"):
        pack = label[5:]
        if pack not in PACKS:
            raise ValueError(f"Unknown pack: {pack}")
        value = {"schema": 1, "name": pack, "extends": [pack], "rules": []}
    else:
        try:
            value = yaml.safe_load(Path(label).expanduser().read_text(encoding="utf-8"))
        except yaml.YAMLError as error:
            raise ValueError("Invalid YAML rule file.") from error
    resolved = resolve(value)
    # Validate the effective rules too, since inherited patches may omit the check type.
    VALIDATOR.validate(
        {
            "schema": 1,
            "name": value["name"],
            "rules": [
                {key: item for key, item in rule.items() if key != "pack"} for rule in resolved
            ],
        }
    )
    digest = hashlib.sha256(
        json.dumps({"source": value, "rules": resolved}, sort_keys=True).encode()
    ).hexdigest()
    return {
        "file": label,
        "sha256": digest,
        "packs": list(dict.fromkeys(packs)),
        "rules": resolved,
        "references": value.get("references", {}),
    }
