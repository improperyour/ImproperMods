#!/usr/bin/env python3

from __future__ import annotations

"""
Search BuildMenu records in either supported JSON shape:
1) Classification tree under root key "exact"
2) Flat array under root key "pieces"

This utility scans leaf arrays and prints matching records. It supports optional
path scoping, configurable searched properties, aligned required-material
output, and optional TXT/CSV export.
"""

import argparse
import csv
import json
import os
import re
import sys
from collections import defaultdict
from pathlib import Path
from typing import Any


ANSI_RESET = "\x1b[0m"
ANSI_PATTERN = re.compile(r"\x1b\[[0-9;]*m")


def parse_args() -> argparse.Namespace:
    epilog = (
        "Examples:\n"
        "  Search all exact leaf arrays (default fields: name,prefab):\n"
        "    python search_build_menu_classification.py --query stone\n"
        "\n"
        "  Search a specific branch under exact:\n"
        "    python search_build_menu_classification.py --query stone --path Misc:Stone\n"
        "\n"
        "  Search custom fields only:\n"
        "    python search_build_menu_classification.py --query workbench --search craftingStation\n"
        "\n"
        "  Show records that have a value for a field (query omitted) - this is case sensitive:\n"
        "    python search_build_menu_classification.py --search systemEffects\n"
        "\n"
        "  Print a single record by global traversal id:\n"
        "    python search_build_menu_classification.py --id 125\n"
        "\n"
        "  Include aligned required materials, write TXT and CSV:\n"
        "    python search_build_menu_classification.py --query stone --path Misc:Stone "
        "--required --output results.txt --csv\n"
        "\n"
        "  Print exact tree from one file:\n"
        "    python search_build_menu_classification.py --tree --files ExampleOutput.json\n"
        "    python search_build_menu_classification.py --tree --show items --files ExampleOutput.json\n"
        "    python search_build_menu_classification.py --tree --show items,filename "
        "--files ExampleOutput.json,Diff.json\n"
        "    python search_build_menu_classification.py --tree --show items,systemeffects,interaction_hooks "
        "--files ExampleOutput.json\n"
        "\n"
        "  Print exact tree merged from all JSON files in a directory:\n"
        "    python search_build_menu_classification.py --tree --dir GameItemJsons\n"
        "    python search_build_menu_classification.py --tree --dir GameItemJsons "
        "--exclude-files vanilla\n"
        "\n"
        "  Print exact tree merged from selected files:\n"
        "    python search_build_menu_classification.py --tree "
        "--files ExampleOutput.json,Diff.json\n"
        "\n"
        "Notes:\n"
        "  - Search is plain text only (no regex).\n"
        "  - Input type is auto-detected: 'exact' tree first, then 'pieces' array.\n"
        "  - In 'pieces' mode, --path may be omitted or set to 'pieces'.\n"
        "  - If --query is omitted, --search is required and matching is based on "
        "field presence.\n"
        "  - --id bypasses query/search and prints one full record by global index.\n"
        "  - --csv requires --output and writes <output_basename>.csv."
    )
    parser = argparse.ArgumentParser(
        description=(
            "Search BuildMenu JSON records in either an 'exact' classification "
            "tree or a 'pieces' array."
        ),
        epilog=epilog,
        formatter_class=argparse.RawTextHelpFormatter,
        allow_abbrev=False,
    )
    parser.add_argument(
        "--query",
        default="",
        help=(
            "Plain-text query to search for (regex is not supported). "
            "If omitted, matching is based on whether searched fields have values."
        ),
    )
    parser.add_argument(
        "--id",
        type=int,
        default=None,
        help=(
            "Global record index to print as pretty JSON. "
            "When set, query/search matching is skipped."
        ),
    )
    input_group = parser.add_mutually_exclusive_group(required=True)
    input_group.add_argument(
        "--dir",
        default="",
        help="Read and merge all .json files from this directory.",
    )
    input_group.add_argument(
        "--files",
        default="",
        help="Comma-separated list of JSON files to read and merge.",
    )
    parser.add_argument(
        "--exclude-files",
        default="",
        help=(
            "Only with --dir: skip files whose filename contains this text "
            "(case-insensitive)."
        ),
    )
    parser.add_argument(
        "--tree",
        action="store_true",
        help="Print exact Top/Subcategory/item tree output instead of search results.",
    )
    parser.add_argument(
        "--show",
        default="",
        help=(
            "In --tree mode, comma-separated display options. "
            "Supported: items, filename, systemeffects, interaction_hooks. "
            "Example: --show items,filename,systemeffects"
        ),
    )
    parser.add_argument(
        "--verbose",
        action="store_true",
        help="In --tree mode, prepend each item line with (Top:Subcategory).",
    )
    parser.add_argument(
        "--path",
        default="",
        help=(
            "Optional colon-separated path. In exact mode, this is resolved "
            "under exact (e.g. 'Misc:Stone'). In pieces mode, omit it or use "
            "'pieces'."
        ),
    )
    parser.add_argument(
        "--case-sensitive",
        action="store_true",
        help="Use case-sensitive matching (default: case-insensitive).",
    )
    parser.add_argument(
        "--search",
        default="",
        help=(
            "Comma-separated record properties to search. "
            "When --query is set, default is name,prefab if omitted. "
            "When --query is omitted, this is required. "
            "Example: --search name,prefab,subcategory"
        ),
    )
    parser.add_argument(
        "--not",
        dest="invert",
        action="store_true",
        help="Invert results (print records that do not match).",
    )
    parser.add_argument(
        "--output",
        default="",
        help=(
            "Optional text output file to write all console lines to "
            "(stdout output still remains enabled)."
        ),
    )
    parser.add_argument(
        "--required",
        action="store_true",
        help=(
            "Append condensed required materials for each result line, formatted as "
            "(Material:Amount, Material:Amount, ...)."
        ),
    )
    parser.add_argument(
        "--csv",
        action="store_true",
        help=(
            "Also write CSV using the same filename as --output but with a .csv extension."
        ),
    )
    return parser.parse_args()


def resolve_config_path(exact_root: Any, path: str) -> tuple[str, Any]:
    if not path.strip():
        return "exact", exact_root

    raw_parts = [part.strip() for part in path.split(":")]
    parts = [part for part in raw_parts if part]
    if not parts:
        return "exact", exact_root

    if parts[0] == "exact":
        parts = parts[1:]

    node = exact_root
    full_parts = ["exact"]
    for part in parts:
        if not isinstance(node, dict):
            raise ValueError(f"Config path is not a tree node at: {':'.join(full_parts)}")
        if part not in node:
            raise ValueError(f"Config path not found: exact:{':'.join(parts)}")
        node = node[part]
        full_parts.append(part)

    return ":".join(full_parts), node


def resolve_pieces_path(path: str) -> str:
    if not path.strip():
        return "pieces"

    raw_parts = [part.strip() for part in path.split(":")]
    parts = [part for part in raw_parts if part]
    if not parts:
        return "pieces"

    if parts[0] in {"exact", "pieces"}:
        parts = parts[1:]

    if parts:
        raise ValueError(
            "In 'pieces' mode, --path does not support nested segments. "
            "Use no --path or --path pieces."
        )
    return "pieces"


def iter_leaf_arrays(node: Any, path_parts: list[str]):
    if isinstance(node, list):
        yield path_parts, node
        return

    if isinstance(node, dict):
        for key, child in node.items():
            yield from iter_leaf_arrays(child, path_parts + [key])


def _merge_exact_tree(
    dest: dict[str, dict[str, list[Any]]],
    exact_tree: Any,
    source: Path,
    source_dest: dict[str, dict[str, list[str]]] | None = None,
) -> tuple[int, int, int]:
    if not isinstance(exact_tree, dict):
        raise ValueError(f"{source}: top-level 'exact' must be an object.")

    top_count = 0
    sub_count = 0
    item_count = 0

    for top, sub_map in exact_tree.items():
        if not isinstance(top, str):
            continue
        top_count += 1
        if not isinstance(sub_map, dict):
            raise ValueError(f"{source}: exact[{top!r}] must be an object.")
        for sub, items in sub_map.items():
            if not isinstance(sub, str):
                continue
            sub_count += 1
            if not isinstance(items, list):
                raise ValueError(f"{source}: exact[{top!r}][{sub!r}] must be a list.")
            dest[top][sub].extend(items)
            if source_dest is not None:
                source_dest[top][sub].extend([source.name] * len(items))
            item_count += len(items)

    return top_count, sub_count, item_count


def resolve_input_paths(args: argparse.Namespace) -> list[Path]:
    files_given = args.files.strip() != ""
    dir_given = args.dir.strip() != ""
    exclude_contains = args.exclude_files.strip().lower()

    if dir_given:
        if files_given:
            raise ValueError("Use either --dir or --files, not both.")
        dir_path = Path(args.dir)
        if not dir_path.is_dir():
            raise FileNotFoundError(f"Directory not found: {dir_path}")
        files = sorted(
            path for path in dir_path.iterdir() if path.is_file() and path.suffix.lower() == ".json"
        )
        if exclude_contains:
            files = [path for path in files if exclude_contains not in path.name.lower()]
        if not files:
            if exclude_contains:
                raise ValueError(
                    f"No .json files remain in {dir_path} after --exclude-files={args.exclude_files!r}."
                )
            raise ValueError(f"No .json files found in directory: {dir_path}")
        return files

    if files_given:
        if exclude_contains:
            raise ValueError("--exclude-files can only be used with --dir.")
        raw_parts = [part.strip() for part in args.files.split(",")]
        file_parts = [part for part in raw_parts if part]
        if not file_parts:
            raise ValueError("--files must contain at least one path.")
        paths = [Path(part) for part in file_parts]
        missing = [path for path in paths if not path.exists()]
        if missing:
            raise FileNotFoundError(f"Input file not found: {missing[0]}")
        not_files = [path for path in paths if not path.is_file()]
        if not_files:
            raise ValueError(f"Input path is not a file: {not_files[0]}")
        return paths

    raise ValueError("Either --dir or --files is required.")


def load_input_data(args: argparse.Namespace) -> tuple[dict[str, Any], list[tuple[str, int, int, int]]]:
    paths = resolve_input_paths(args)
    merged_exact: dict[str, dict[str, list[Any]]] = defaultdict(lambda: defaultdict(list))
    merged_exact_sources: dict[str, dict[str, list[str]]] = defaultdict(lambda: defaultdict(list))
    merged_pieces: list[dict[str, Any]] = []
    seen_exact = False
    seen_pieces = False
    file_stats: list[tuple[str, int, int, int]] = []

    for path in paths:
        with path.open("r", encoding="utf-8") as f:
            data = json.load(f)
        if not isinstance(data, dict):
            raise ValueError(f"{path}: top-level JSON must be an object.")

        top_count = 0
        sub_count = 0
        item_count = 0
        if "exact" in data:
            top_count, sub_count, item_count = _merge_exact_tree(
                merged_exact, data["exact"], path, merged_exact_sources
            )
            seen_exact = True

        if "pieces" in data:
            pieces = data["pieces"]
            if not isinstance(pieces, list):
                raise ValueError(f"{path}: top-level 'pieces' key must contain an array.")
            if not all(isinstance(item, dict) for item in pieces):
                raise ValueError(f"{path}: top-level 'pieces' array must contain object records only.")
            merged_pieces.extend(pieces)
            seen_pieces = True
        file_stats.append((path.name, top_count, sub_count, item_count))

    if not seen_exact and not seen_pieces:
        if len(paths) == 1:
            raise ValueError("Top-level JSON must contain either an 'exact' or 'pieces' key.")
        raise ValueError("None of the input files contain a top-level 'exact' or 'pieces' key.")

    out: dict[str, Any] = {}
    if seen_exact:
        out["exact"] = {top: dict(sub_map) for top, sub_map in merged_exact.items()}
        out["_exact_sources"] = {top: dict(sub_map) for top, sub_map in merged_exact_sources.items()}
    if seen_pieces:
        out["pieces"] = merged_pieces
    return out, file_stats


def contains_query(value: Any, query: str, case_sensitive: bool) -> bool:
    if isinstance(value, str):
        if case_sensitive:
            return query in value
        return query.lower() in value.lower()

    if isinstance(value, dict):
        return any(contains_query(v, query, case_sensitive) for v in value.values())

    if isinstance(value, list):
        return any(contains_query(v, query, case_sensitive) for v in value)

    return False


def parse_search_fields(raw: str) -> list[str]:
    fields = [part.strip() for part in raw.split(",") if part.strip()]
    if not fields:
        raise ValueError("--search must include at least one property name.")
    return fields


def has_value(value: Any) -> bool:
    if value is None:
        return False
    if isinstance(value, str):
        return value.strip() != ""
    if isinstance(value, (list, dict, tuple, set)):
        return len(value) > 0
    return True


def record_matches_presence(item: Any, search_fields: list[str]) -> bool:
    if not isinstance(item, dict):
        return False
    for field in search_fields:
        if field in item and has_value(item[field]):
            return True
    return False


def format_presence_fields(item: Any, search_fields: list[str]) -> str:
    if not isinstance(item, dict):
        return ""

    parts: list[str] = []
    for field in search_fields:
        if field not in item or not has_value(item[field]):
            continue
        value = item[field]
        if isinstance(value, str):
            rendered = value
        else:
            rendered = json.dumps(value, ensure_ascii=False)
        parts.append(f"{field}={rendered}")

    if not parts:
        return ""
    return f" | {'; '.join(parts)}"


def record_matches_query(
    item: Any, query: str, case_sensitive: bool, search_fields: list[str]
) -> bool:
    if not isinstance(item, dict):
        return False

    for field in search_fields:
        if field in item and contains_query(item[field], query, case_sensitive):
            return True
    return False


def item_label(item: Any) -> str:
    if isinstance(item, dict):
        name = item.get("name")
        prefab = item.get("prefab")
        if isinstance(name, str) and isinstance(prefab, str):
            return f"name={name!r}, prefab={prefab!r}"
        if isinstance(name, str):
            return f"name={name!r}"
        if isinstance(prefab, str):
            return f"prefab={prefab!r}"
    return json.dumps(item, ensure_ascii=False)[:200]


def format_required_suffix(item: Any) -> str:
    if not isinstance(item, dict):
        return ""
    required = item.get("required")
    if not isinstance(required, list) or not required:
        return ""

    parts: list[str] = []
    for entry in required:
        if not isinstance(entry, dict):
            continue
        req = entry.get("required")
        amount = entry.get("amount")
        if isinstance(req, str) and amount is not None:
            parts.append(f"{req}:{amount}")

    if not parts:
        return ""
    return f" ({', '.join(parts)})"


def item_tree_parts(item: Any) -> tuple[str, str | None]:
    if isinstance(item, dict):
        name = item.get("name")
        prefab = item.get("prefab")
        name_text = name.strip() if isinstance(name, str) else ""
        prefab_text = prefab.strip() if isinstance(prefab, str) else ""
        if name_text and prefab_text:
            return name_text, prefab_text
        if name_text:
            return name_text, None
        if prefab_text:
            return prefab_text, None
    return json.dumps(item, ensure_ascii=False), None


def tree_glyphs() -> tuple[str, str, str]:
    encoding = sys.stdout.encoding or ""
    try:
        "├└│".encode(encoding)
        return "├─", "└─", "│  "
    except (UnicodeEncodeError, LookupError):
        return "|-", "\\-", "|  "


def tree_palette() -> dict[str, str] | None:
    no_color = os.environ.get("NO_COLOR")
    if no_color is not None:
        return None

    force_color = os.environ.get("FORCE_COLOR", "").strip()
    if force_color and force_color != "0":
        if force_color in {"2", "3"}:
            return {
                "top": "38;5;10",
                "sub": "38;5;34",
                "count": "38;5;250",
                "name": "38;5;15",
                "prefab": "38;5;252",
                "totaltop": "38;5;142",
                "footerfiles": "38;5;136",
            }
        return {
            "top": "92",
            "sub": "32",
            "count": "37",
            "name": "97",
            "prefab": "37",
            "totaltop": "33",
            "footerfiles": "33",
        }

    if not sys.stdout.isatty():
        return None

    term = os.environ.get("TERM", "").lower()
    colorterm = os.environ.get("COLORTERM", "").lower()
    supports_extended = "256color" in term or "truecolor" in colorterm or "24bit" in colorterm
    if supports_extended:
        return {
            "top": "38;5;46",
            "sub": "38;5;70",
            "count": "38;5;238",
            "name": "38;5;252",
            "prefab": "38;5;66",
            "totaltop": "38;5;142",
            "footerfiles": "38;5;136",
        }
    return {
        "top": "92",
        "sub": "32",
        "count": "37",
        "name": "97",
        "prefab": "37",
        "totaltop": "33",
        "footerfiles": "33",
    }


def paint(text: str, role: str, palette: dict[str, str] | None) -> str:
    if palette is None:
        return text
    code = palette.get(role)
    if not code:
        return text
    return f"\x1b[{code}m{text}{ANSI_RESET}"


def strip_ansi(text: str) -> str:
    return ANSI_PATTERN.sub("", text)


def parse_show_options(raw: str) -> set[str]:
    if not raw.strip():
        return set()
    alias_map = {
        "items": "items",
        "filename": "filename",
        "systemeffects": "systemeffects",
        "system_effects": "systemeffects",
        "interaction_hooks": "interaction_hooks",
        "interactionhooks": "interaction_hooks",
    }
    options: set[str] = set()
    invalid: list[str] = []
    for raw_part in (part.strip().lower() for part in raw.split(",") if part.strip()):
        normalized = alias_map.get(raw_part)
        if normalized is None:
            invalid.append(raw_part)
            continue
        options.add(normalized)
    allowed = {"items", "filename", "systemeffects", "interaction_hooks"}
    invalid.extend(sorted(option for option in options if option not in allowed))
    if invalid:
        raise ValueError(
            f"--show contains unsupported values: {', '.join(invalid)}. "
            "Supported values are: items, filename, systemeffects, interaction_hooks."
        )
    return options


def build_tree_lines(
    exact_root: Any,
    path: str,
    file_stats: list[tuple[str, int, int, int]],
    include_items: bool = False,
    show_filename: bool = False,
    show_systemeffects: bool = False,
    show_interaction_hooks: bool = False,
    show_required: bool = False,
    verbose: bool = False,
    exact_sources: Any = None,
) -> list[str]:
    if not isinstance(exact_root, dict):
        raise ValueError("Top-level 'exact' key must contain an object.")

    base_path, tree_node = resolve_config_path(exact_root, path)
    grouped: dict[str, dict[str, list[Any]]] = defaultdict(lambda: defaultdict(list))
    grouped_sources: dict[str, dict[str, list[str]]] = defaultdict(lambda: defaultdict(list))
    branch_mid, branch_last, vertical = tree_glyphs()
    palette = tree_palette()

    for leaf_path_parts, records in iter_leaf_arrays(tree_node, base_path.split(":")):
        normalized = leaf_path_parts[1:] if leaf_path_parts and leaf_path_parts[0] == "exact" else leaf_path_parts
        if not normalized:
            continue
        top = normalized[0]
        sub = normalized[1] if len(normalized) > 1 else "(items)"
        grouped[top][sub].extend(records)

    if include_items and show_filename and isinstance(exact_sources, dict):
        _, source_node = resolve_config_path(exact_sources, path)
        for leaf_path_parts, source_records in iter_leaf_arrays(source_node, base_path.split(":")):
            normalized = (
                leaf_path_parts[1:]
                if leaf_path_parts and leaf_path_parts[0] == "exact"
                else leaf_path_parts
            )
            if not normalized:
                continue
            top = normalized[0]
            sub = normalized[1] if len(normalized) > 1 else "(items)"
            for src in source_records:
                grouped_sources[top][sub].append(src if isinstance(src, str) else "unknown")

    lines: list[str] = []
    tops = sorted(grouped)
    for top in tops:
        sub_count = len(grouped[top])
        top_item_count = sum(len(grouped[top][sub]) for sub in grouped[top])
        lines.append(
            f"{paint(top, 'top', palette)} "
            f"{paint(f'({sub_count} subcategories, {top_item_count} items)', 'count', palette)}"
        )
        sub_names = sorted(grouped[top])
        for sub_index, sub in enumerate(sub_names):
            is_last_sub = sub_index == len(sub_names) - 1
            sub_branch = branch_last if is_last_sub else branch_mid
            items = grouped[top][sub]
            item_count = len(items)
            lines.append(
                f"  {sub_branch} {paint(sub, 'sub', palette)} "
                f"{paint(f'({item_count} items)', 'count', palette)}"
            )

            if include_items:
                item_prefix = "   " if is_last_sub else vertical
                for item_index, item in enumerate(items):
                    is_last_item = item_index == len(items) - 1
                    item_branch = branch_last if is_last_item else branch_mid
                    name, prefab = item_tree_parts(item)
                    system_effects_text = ""
                    interaction_hooks_text = ""
                    if prefab:
                        item_text = (
                            f"{paint(name, 'name', palette)} "
                            f"({paint(prefab, 'prefab', palette)})"
                        )
                    else:
                        item_text = paint(name, "name", palette)

                    if show_systemeffects:
                        system_effects = []
                        if isinstance(item, dict):
                            raw_effects = item.get("system_effects", [])
                            if isinstance(raw_effects, list):
                                system_effects = [str(value) for value in raw_effects]
                        effects_joined = ", ".join(system_effects) if system_effects else "none"
                        system_effects_text = f" systemeffects=[{effects_joined}]"

                    if show_interaction_hooks:
                        interaction_hooks = []
                        if isinstance(item, dict):
                            raw_hooks = item.get("interaction_hooks", [])
                            if isinstance(raw_hooks, list):
                                interaction_hooks = [str(value) for value in raw_hooks]
                        hooks_joined = ", ".join(interaction_hooks) if interaction_hooks else "none"
                        interaction_hooks_text = f" interaction_hooks=[{hooks_joined}]"

                    if show_filename:
                        src = (
                            grouped_sources[top][sub][item_index]
                            if item_index < len(grouped_sources[top][sub])
                            else "unknown"
                        )
                        item_text = f"{item_text} {paint(f'[{src}]', 'footerfiles', palette)}"
                    if system_effects_text:
                        item_text = f"{item_text}{paint(system_effects_text, 'count', palette)}"
                    if interaction_hooks_text:
                        item_text = f"{item_text}{paint(interaction_hooks_text, 'count', palette)}"
                    if show_required:
                        required_suffix = format_required_suffix(item)
                        if required_suffix:
                            item_text = f"{item_text}{paint(required_suffix, 'count', palette)}"
                    if verbose:
                        item_text = f"{paint(f'({top}:{sub})', 'count', palette)} {item_text}"
                    lines.append(f"  {item_prefix}{item_branch} {item_text}")
    total_top = len(grouped)
    total_subcategories = sum(len(grouped[top]) for top in grouped)
    total_items = sum(len(grouped[top][sub]) for top in grouped for sub in grouped[top])

    smallest_subs_line = "Subcategories Per Top: smallest = 0 (n/a), largest = 0 (n/a)"
    if grouped:
        subcounts_per_top = [
            (len(grouped[top]), top)
            for top in sorted(grouped)
        ]
        smallest_sub_count, smallest_sub_top = min(subcounts_per_top, key=lambda x: (x[0], x[1]))
        largest_sub_count, largest_sub_top = max(subcounts_per_top, key=lambda x: (x[0], x[1]))
        smallest_subs_line = (
            "Subcategories Per Top: "
            f"smallest = {smallest_sub_count} ({smallest_sub_top}), "
            f"largest = {largest_sub_count} ({largest_sub_top})"
        )

    smallest_items_line = "Items Per Subcategory: smallest = 0 (n/a), largest = 0 (n/a)"
    if grouped:
        items_per_subcategory = [
            (len(grouped[top][sub]), top, sub)
            for top in sorted(grouped)
            for sub in sorted(grouped[top])
        ]
        smallest_item_count, smallest_item_top, smallest_item_sub = min(
            items_per_subcategory, key=lambda x: (x[0], x[1], x[2])
        )
        largest_item_count, largest_item_top, largest_item_sub = max(
            items_per_subcategory, key=lambda x: (x[0], x[1], x[2])
        )
        smallest_items_line = (
            "Items Per Subcategory: "
            f"smallest = {smallest_item_count} ({smallest_item_top}:{smallest_item_sub}), "
            f"largest = {largest_item_count} ({largest_item_top}:{largest_item_sub})"
        )

    lines.append("")
    lines.append(paint(f"Total Top Level = {total_top}", "totaltop", palette))
    lines.append(paint(f"Total Subcategories = {total_subcategories}", "totaltop", palette))
    lines.append(paint(smallest_subs_line, "totaltop", palette))
    lines.append(paint(f"Total Items = {total_items}", "totaltop", palette))
    lines.append(paint(smallest_items_line, "totaltop", palette))
    lines.append("")
    for filename, top_count, sub_count, item_count in file_stats:
        lines.append(
            paint(
                f"-> {filename}; {top_count} tops, {sub_count} subcategories, {item_count} items",
                "footerfiles",
                palette,
            )
        )
    return lines


def main() -> int:
    args = parse_args()
    data, file_stats = load_input_data(args)

    if args.tree:
        if args.id is not None:
            raise ValueError("--tree cannot be combined with --id.")
        if args.query.strip():
            raise ValueError("--tree cannot be combined with --query.")
        if args.search.strip():
            raise ValueError("--tree cannot be combined with --search.")
        if args.invert:
            raise ValueError("--tree cannot be combined with --not.")
        if args.csv:
            raise ValueError("--tree cannot be combined with --csv.")
        if "exact" not in data:
            raise ValueError("Tree mode requires input containing top-level 'exact'.")

        show_options = parse_show_options(args.show)
        include_items = any(
            option in show_options
            for option in {"items", "filename", "systemeffects", "interaction_hooks"}
        )
        tree_lines = build_tree_lines(
            data["exact"],
            args.path,
            file_stats,
            include_items=include_items,
            show_filename="filename" in show_options,
            show_systemeffects="systemeffects" in show_options,
            show_interaction_hooks="interaction_hooks" in show_options,
            show_required=args.required,
            verbose=args.verbose,
            exact_sources=data.get("_exact_sources"),
        )
        for line in tree_lines:
            print(line)

        if args.output.strip():
            output_path = Path(args.output)
            output_path.parent.mkdir(parents=True, exist_ok=True)
            with output_path.open("w", encoding="utf-8") as f:
                if tree_lines:
                    f.write("\n".join(strip_ansi(line) for line in tree_lines))
                    f.write("\n")
        return 0

    if "exact" in data:
        exact_root = data["exact"]
        base_path, search_node = resolve_config_path(exact_root, args.path)
    elif "pieces" in data:
        pieces = data["pieces"]
        if not isinstance(pieces, list):
            raise ValueError("Top-level 'pieces' key must contain an array.")
        if not all(isinstance(item, dict) for item in pieces):
            raise ValueError("Top-level 'pieces' array must contain object records only.")
        base_path = resolve_pieces_path(args.path)
        search_node = pieces
    else:
        raise ValueError("Top-level JSON must contain either an 'exact' or 'pieces' key.")

    query_mode = args.query.strip() != ""
    search_fields: list[str] = []
    if args.id is None:
        if query_mode:
            search_raw = args.search if args.search.strip() else "name,prefab"
            search_fields = parse_search_fields(search_raw)
        else:
            if not args.search.strip():
                raise ValueError("Either --query or --search must be provided.")
            search_fields = parse_search_fields(args.search)

    if args.csv and not args.output.strip():
        raise ValueError("--csv requires --output so the CSV filename can be derived.")

    output_lines: list[str] = []
    csv_rows: list[dict[str, str]] = []

    def emit(line: str = "") -> None:
        print(line)
        output_lines.append(line)

    if args.id is not None:
        if args.id < 0:
            raise ValueError("--id must be a non-negative integer.")

        current_id = 0
        found_item: Any | None = None
        found_leaf_path = ""
        found_leaf_index = -1

        for path_parts, records in iter_leaf_arrays(search_node, base_path.split(":")):
            for index, item in enumerate(records):
                if current_id == args.id:
                    found_item = item
                    found_leaf_path = ":".join(path_parts)
                    found_leaf_index = index
                    break
                current_id += 1
            if found_item is not None:
                break

        if found_item is None:
            max_id = current_id - 1
            raise ValueError(
                f"--id {args.id} is out of range. "
                f"Available record ids under {base_path}: 0..{max_id}"
            )

        emit(f"record_id={args.id}")
        emit(f"leaf_path={found_leaf_path}")
        emit(f"leaf_index={found_leaf_index}")
        emit()
        emit("record_json:")
        for line in json.dumps(found_item, indent=2, ensure_ascii=False).splitlines():
            emit(line)

        if args.csv:
            required_suffix = format_required_suffix(found_item)
            csv_rows.append(
                {
                    "leaf_path": found_leaf_path,
                    "index": str(found_leaf_index),
                    "label": item_label(found_item),
                    "required": required_suffix[2:-1] if required_suffix else "",
                    "line": f"record_id={args.id}",
                }
            )

        if args.output.strip():
            output_path = Path(args.output)
            output_path.parent.mkdir(parents=True, exist_ok=True)
            with output_path.open("w", encoding="utf-8") as f:
                f.write("\n".join(output_lines))
                f.write("\n")

            if args.csv:
                csv_path = output_path.with_suffix(".csv")
                with csv_path.open("w", encoding="utf-8", newline="") as f:
                    writer = csv.DictWriter(
                        f, fieldnames=["leaf_path", "index", "label", "required", "line"]
                    )
                    writer.writeheader()
                    writer.writerows(csv_rows)
        return 0

    total_arrays = 0
    total_matches = 0
    printed_leaf_count = 0
    show_presence_values = args.id is None and (not query_mode) and (not args.invert)
    for path_parts, records in iter_leaf_arrays(search_node, base_path.split(":")):
        total_arrays += 1
        matches: list[tuple[int, Any]] = []
        for index, item in enumerate(records):
            if query_mode:
                is_match = record_matches_query(
                    item, args.query, args.case_sensitive, search_fields
                )
            else:
                is_match = record_matches_presence(item, search_fields)

            if args.invert:
                is_match = not is_match

            if is_match:
                matches.append((index, item))

        if not matches:
            continue

        leaf_path = ":".join(path_parts)
        if printed_leaf_count > 0:
            emit()
        emit(f"{leaf_path} ({len(matches)} matches)")
        printed_leaf_count += 1

        labels = [item_label(item) for _, item in matches]
        display_prefixes = [f"[{index}] {label}" for (index, _), label in zip(matches, labels)]
        max_prefix_len = max(len(prefix) for prefix in display_prefixes)

        for (index, item), label, prefix in zip(matches, labels, display_prefixes):
            required_suffix = format_required_suffix(item)
            spacing = " " * (max_prefix_len - len(prefix) + 4) if args.required else ""
            presence_suffix = (
                format_presence_fields(item, search_fields) if show_presence_values else ""
            )
            line = (
                f"  {prefix}{presence_suffix}{spacing}{required_suffix}"
                if args.required
                else f"  {prefix}{presence_suffix}"
            )
            emit(line)

            if args.csv:
                csv_rows.append(
                    {
                        "leaf_path": leaf_path,
                        "index": str(index),
                        "label": label,
                        "required": required_suffix[2:-1] if required_suffix else "",
                        "line": line.strip(),
                    }
                )
        total_matches += len(matches)

    emit()
    emit(
        f"Searched {total_arrays} leaf arrays under {base_path}. "
        f"Found {total_matches} matching records."
    )

    if args.output.strip():
        output_path = Path(args.output)
        output_path.parent.mkdir(parents=True, exist_ok=True)
        with output_path.open("w", encoding="utf-8") as f:
            f.write("\n".join(output_lines))
            f.write("\n")

        if args.csv:
            csv_path = output_path.with_suffix(".csv")
            with csv_path.open("w", encoding="utf-8", newline="") as f:
                writer = csv.DictWriter(
                    f, fieldnames=["leaf_path", "index", "label", "required", "line"]
                )
                writer.writeheader()
                writer.writerows(csv_rows)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
