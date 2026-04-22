#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path
from typing import Any


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Compare two JSON files containing a top-level 'pieces' array."
    )
    parser.add_argument(
        "--input",
        nargs=2,
        metavar=("FILE1", "FILE2"),
        help="Two input JSON files to compare.",
    )
    parser.add_argument(
        "--dir",
        help="Directory containing one or more .json files to combine as input1.",
    )
    parser.add_argument(
        "--file",
        help="Single JSON file to compare against the combined --dir input.",
    )
    parser.add_argument(
        "--output",
        required=True,
        help="Output JSON path for differing records.",
    )
    parser.add_argument(
        "--debug",
        action="store_true",
        help="Print detailed item-level duplicate/export information.",
    )
    args = parser.parse_args()

    using_pair_mode = args.input is not None
    using_dir_mode = args.dir is not None or args.file is not None

    if using_pair_mode and using_dir_mode:
        parser.error("Use either --input FILE1 FILE2 or --dir DIR --file FILE, not both.")
    if not using_pair_mode and not using_dir_mode:
        parser.error("You must provide --input FILE1 FILE2 or --dir DIR --file FILE.")
    if using_dir_mode and (args.dir is None or args.file is None):
        parser.error("Directory mode requires both --dir DIR and --file FILE.")

    return args


def load_pieces(path: Path, debug: bool) -> list[dict[str, Any]]:
    with path.open("r", encoding="utf-8") as f:
        data = json.load(f)

    if not isinstance(data, dict):
        raise ValueError(f"{path} must contain a top-level JSON object.")
    pieces = data.get("pieces")
    if not isinstance(pieces, list):
        raise ValueError(f"{path} must contain a top-level 'pieces' array.")
    if not all(isinstance(record, dict) for record in pieces):
        raise ValueError(f"{path} contains non-object entries in 'pieces'.")
    if debug:
        print(f"[debug] Loaded {len(pieces)} pieces from file {path}")
    return pieces


def load_pieces_from_dir(path: Path, debug: bool) -> list[dict[str, Any]]:
    if not path.is_dir():
        raise ValueError(f"{path} is not a directory.")

    json_files = sorted(p for p in path.iterdir() if p.is_file() and p.suffix.lower() == ".json")
    if not json_files:
        raise ValueError(f"{path} does not contain any .json files.")

    combined: list[dict[str, Any]] = []
    for json_file in json_files:
        combined.extend(load_pieces(json_file, debug))
    if debug:
        print(f"[debug] Loaded a combined {len(combined)} pieces from file {path}")
    return combined


def normalize_piece_table(value: Any) -> Any:
    if not isinstance(value, str):
        return value

    # Ignore HammerPieceTable entries (with optional leading underscore),
    # then ignore commas and whitespace for comparison purposes.
    cleaned = re.sub(r"_?HammerPieceTable", "", value, flags=re.IGNORECASE)
    cleaned = re.sub(r"[\s,]+", "", cleaned)
    return cleaned


def canonical_record(record: dict[str, Any]) -> str:
    normalized = dict(record)
    normalized.pop("category", None)
    if "pieceTable" in normalized:
        normalized["pieceTable"] = normalize_piece_table(normalized["pieceTable"])
    return json.dumps(normalized, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def export_record(record: dict[str, Any]) -> dict[str, Any]:
    exported = dict(record)
    exported["category"] = 0
    return exported


def debug_record(record: dict[str, Any]) -> str:
    return json.dumps(record, sort_keys=True, ensure_ascii=False)


def item_id(record: dict[str, Any]) -> str:
    prefab = record.get("Prefab")
    if isinstance(prefab, str) and prefab.strip():
        return prefab

    token = record.get("token")
    if isinstance(token, str) and token.strip():
        return token

    name = record.get("name")
    if isinstance(name, str) and name.strip():
        return name

    return canonical_record(record)


def index_by_item_id(
    pieces: list[dict[str, Any]], label: str, debug: bool
) -> dict[str, dict[str, Any]]:
    indexed: dict[str, dict[str, Any]] = {}
    duplicate_ids: list[str] = []

    for record in pieces:
        key = item_id(record)
        if key in indexed:
            duplicate_ids.append(key)
            continue
        indexed[key] = record

    if debug and duplicate_ids:
        print(f"[debug] Duplicate item ids inside {label} (ignored after first):")
        for key in sorted(set(duplicate_ids)):
            print(f"  - {key}")

    return indexed


def compare_pieces(
    left: list[dict[str, Any]], right: list[dict[str, Any]], debug: bool
) -> tuple[list[dict[str, Any]], dict[str, int]]:
    left_index = index_by_item_id(left, "file1", debug)
    right_index = index_by_item_id(right, "file2", debug)

    all_ids = sorted(set(left_index) | set(right_index))
    exported: list[dict[str, Any]] = []

    duplicate_match_count = 0
    only_file1_count = 0
    only_file2_count = 0
    changed_count = 0

    if debug:
        print("[debug] Exact duplicates (same item id + same full record):")

    for key in all_ids:
        in_left = key in left_index
        in_right = key in right_index

        if in_left and in_right:
            left_record = left_index[key]
            right_record = right_index[key]
            if canonical_record(left_record) == canonical_record(right_record):
                duplicate_match_count += 1
                if debug:
                    print(f"  - {key}")
            else:
                changed_count += 1
                exported_record = export_record(right_record)
                exported.append(exported_record)
                if debug:
                    print(f"[debug] Changed item: {key}")
                    print("  [debug] legacy symmetric-diff would export BOTH records:")
                    print(f"    - file1 (suppressed now): {debug_record(left_record)}")
                    print(f"    - file2 (exported now):   {debug_record(exported_record)}")
            continue

        if in_left:
            only_file1_count += 1
            exported.append(export_record(left_index[key]))
            if debug:
                print(f"[debug] Exported file1-only item: {key}")
            continue

        only_file2_count += 1
        exported.append(export_record(right_index[key]))
        if debug:
            print(f"[debug] Exported file2-only item: {key}")

    stats = {
        "duplicates": duplicate_match_count,
        "changed": changed_count,
        "only_file1": only_file1_count,
        "only_file2": only_file2_count,
        "exported": len(exported),
    }

    return exported, stats


def main() -> None:
    args = parse_args()
    output = Path(args.output)

    if args.input is not None:
        input1 = Path(args.input[0])
        input2 = Path(args.input[1])
        pieces1 = load_pieces(input1, args.debug)
        pieces2 = load_pieces(input2, args.debug)
        left_label = f"file1 ({input1})"
        right_label = f"file2 ({input2})"
    else:
        input_dir = Path(args.dir)
        input_file = Path(args.file)
        pieces1 = load_pieces_from_dir(input_dir, args.debug)
        pieces2 = load_pieces(input_file, args.debug)
        left_label = f"dir aggregate ({input_dir})"
        right_label = f"file ({input_file})"

    differences, stats = compare_pieces(pieces1, pieces2, args.debug)

    payload = {"pieces": differences}
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("w", encoding="utf-8") as f:
        json.dump(payload, f, indent=2, ensure_ascii=False)
        f.write("\n")

    print(f"Input {left_label} items: {len(pieces1)}")
    print(f"Input {right_label} items: {len(pieces2)}")
    print(f"Exact duplicates found: {stats['duplicates']}")
    print(f"Changed items found: {stats['changed']}")
    print(f"File1-only items found: {stats['only_file1']}")
    print(f"File2-only items found: {stats['only_file2']}")
    print(f"Unique items exported: {stats['exported']}")
    print(f"Wrote: {output}")


if __name__ == "__main__":
    main()
