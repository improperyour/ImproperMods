#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any


def _strip_comment_keys(value: Any) -> Any:
    if isinstance(value, dict):
        out: dict[str, Any] = {}
        for key, child in value.items():
            if key == "_comment":
                continue
            out[key] = _strip_comment_keys(child)
        return out
    if isinstance(value, list):
        return [_strip_comment_keys(item) for item in value]
    return value


def _extract_rules_list(container: Any, section: str) -> list[dict[str, Any]]:
    if not isinstance(container, dict):
        raise ValueError(f"'{section}' must be an object with a 'rules' array.")
    rules = container.get("rules")
    if not isinstance(rules, list):
        raise ValueError(f"'{section}.rules' must be an array.")
    out: list[dict[str, Any]] = []
    for idx, entry in enumerate(rules):
        if not isinstance(entry, dict):
            raise ValueError(f"'{section}.rules[{idx}]' must be an object.")
        out.append(entry)
    return out


def _parse_target(raw: str) -> tuple[str, str]:
    parts = [part.strip() for part in raw.split(":", 1)]
    if len(parts) != 2 or not parts[0] or not parts[1]:
        raise ValueError("Target must be in the form TOP:SUBCATEGORY.")
    return parts[0], parts[1]


def _csv_patterns(patterns: list[str]) -> str:
    return ", ".join(patterns)


def _target_text(top: str, subcategory: str) -> str:
    return f"{top}:{subcategory}"


def _append_placement_rules(
    rows: list[dict[str, str]],
    overrides: dict[str, Any],
    target_top: str,
    target_sub: str,
) -> None:
    rules = _extract_rules_list(
        overrides.get("placement_rules"),
        "overrides.placement_rules",
    )
    for entry in rules:
        property_name = str(entry.get("property", "")).strip()
        match_type = str(entry.get("match_type", "")).strip()
        patterns_raw = entry.get("patterns", [])
        forced_top = str(entry.get("top", "")).strip()
        forced_sub = entry.get("subcategory")
        forced_sub_text = None if forced_sub is None else str(forced_sub).strip()

        if not property_name or not match_type or not forced_top or not isinstance(patterns_raw, list):
            continue
        patterns = [str(x) for x in patterns_raw]

        include = False
        if forced_sub_text is None:
            include = forced_top == target_top
        else:
            include = forced_top == target_top and forced_sub_text == target_sub

        if not include:
            continue

        out_sub = forced_sub_text if forced_sub_text is not None else target_sub
        rows.append(
            {
                "stage": "placement",
                "node": _target_text(forced_top, out_sub),
                "property": property_name,
                "match_type": match_type,
                "patterns": _csv_patterns(patterns),
            }
        )


def _append_subcategory_rules(
    rows: list[dict[str, str]],
    overrides: dict[str, Any],
    target_top: str,
    target_sub: str,
) -> None:
    rules = _extract_rules_list(
        overrides.get("subcategory_overrides"),
        "overrides.subcategory_overrides",
    )
    for entry in rules:
        property_name = str(entry.get("property", "")).strip()
        pattern = str(entry.get("pattern", "")).strip()
        forced_sub = str(entry.get("subcategory", "")).strip()
        top_filter_raw = entry.get("top")
        top_filter = None if top_filter_raw is None else str(top_filter_raw).strip()
        if not property_name or not pattern or not forced_sub:
            continue

        if forced_sub != target_sub:
            continue
        if top_filter is not None and top_filter != target_top:
            continue

        rows.append(
            {
                "stage": "subcategory",
                "node": _target_text(target_top, forced_sub),
                "property": property_name,
                "match_type": "regex",
                "patterns": pattern,
            }
        )


def _print_rows(rows: list[dict[str, str]]) -> None:
    if not rows:
        return
    node_w = max(len(row["node"]) for row in rows)
    prop_w = max(len(row["property"]) for row in rows)
    match_w = max(len(row["match_type"]) for row in rows)
    for row in rows:
        print(
            f"{row['node'].ljust(node_w)}|"
            f"{row['property'].ljust(prop_w)}|"
            f"{row['match_type'].ljust(match_w)}|"
            f"{row['patterns']}"
        )


def _collect_rows_for_target(
    overrides: dict[str, Any],
    target_top: str,
    target_sub: str,
) -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []
    _append_placement_rules(rows, overrides, target_top, target_sub)
    _append_subcategory_rules(rows, overrides, target_top, target_sub)
    return rows


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Print matching placement rules in processing order for a target TOP:SUBCATEGORY."
        )
    )
    parser.add_argument("--input", required=True, help="Path to config JSON.")
    parser.add_argument("--target", required=False, help="Target in TOP:SUBCATEGORY form.")
    return parser.parse_args()


def _collect_available_targets(overrides: dict[str, Any]) -> list[str]:
    targets: list[str] = []
    seen: set[str] = set()

    def add_target(top: str, sub: str) -> None:
        if not top or not sub:
            return
        target = _target_text(top, sub)
        if target in seen:
            return
        seen.add(target)
        targets.append(target)

    placement_rules = _extract_rules_list(
        overrides.get("placement_rules"),
        "overrides.placement_rules",
    )
    for entry in placement_rules:
        top = str(entry.get("top", "")).strip()
        sub_raw = entry.get("subcategory")
        sub = None if sub_raw is None else str(sub_raw).strip()
        if top and sub:
            add_target(top, sub)

    sub_rules = _extract_rules_list(
        overrides.get("subcategory_overrides"),
        "overrides.subcategory_overrides",
    )
    for entry in sub_rules:
        top_raw = entry.get("top")
        top = None if top_raw is None else str(top_raw).strip()
        sub = str(entry.get("subcategory", "")).strip()
        if top and sub:
            add_target(top, sub)

    return targets


def _summarize_criteria(rows: list[dict[str, str]]) -> str:
    if not rows:
        return ""

    parts: list[str] = []
    for row in rows:
        stage = row.get("stage", "")
        prop = row.get("property", "")
        match_type = row.get("match_type", "")
        patterns = row.get("patterns", "")
        token = f"{stage}|{prop}|{match_type}|{patterns}"
        parts.append(token)

    return "; ".join(parts)


def _print_target_summaries(overrides: dict[str, Any]) -> None:
    targets = _collect_available_targets(overrides)
    if not targets:
        return

    summaries: dict[str, str] = {}
    for target in targets:
        top, sub = _parse_target(target)
        rows = _collect_rows_for_target(overrides, top, sub)
        summaries[target] = _summarize_criteria(rows)

    target_w = max(len(t) for t in targets)
    for target in targets:
        print(f"{target.ljust(target_w)}|{summaries[target]}")


def main() -> int:
    args = parse_args()
    input_path = Path(args.input)
    if not input_path.exists():
        raise FileNotFoundError(f"Input file not found: {input_path}")

    with input_path.open("r", encoding="utf-8") as f:
        config = _strip_comment_keys(json.load(f))

    if not isinstance(config, dict):
        raise ValueError("Top-level JSON must be an object.")
    overrides = config.get("overrides")
    if not isinstance(overrides, dict):
        raise ValueError("Config is missing an 'overrides' object.")

    if not args.target or not str(args.target).strip():
        _print_target_summaries(overrides)
        return 0

    target_top, target_sub = _parse_target(args.target)

    rows = _collect_rows_for_target(overrides, target_top, target_sub)
    _print_rows(rows)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
