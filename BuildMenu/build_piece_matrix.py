#!/usr/bin/env python3

from __future__ import annotations

import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any, Counter as CounterType, TypeAlias


# ============================================================
# TYPE ALIASES
# ============================================================

MaterialName: TypeAlias = str
TopCategoryName: TypeAlias = str
SubCategoryName: TypeAlias = str
PrefabName: TypeAlias = str
PieceName: TypeAlias = str
TokenString: TypeAlias = str
CraftingStationName: TypeAlias = str
InteractionHookName: TypeAlias = str
SourceCategoryName: TypeAlias = str

RequiredItem: TypeAlias = tuple[MaterialName, int]

RawRequiredEntry: TypeAlias = dict[str, Any]
RawPieceRecord: TypeAlias = dict[str, Any]
RawJsonDocument: TypeAlias = dict[str, Any]

NormalizedPieceRecord: TypeAlias = dict[str, Any]
MatrixRecord: TypeAlias = dict[str, Any]
MatrixSubMap: TypeAlias = dict[SubCategoryName, list[MatrixRecord]]
Matrix: TypeAlias = dict[TopCategoryName, MatrixSubMap]

DiagnosticsRecord: TypeAlias = dict[str, Any]
Diagnostics: TypeAlias = dict[str, Any]

MaterialCounter: TypeAlias = CounterType[MaterialName]


# ============================================================
# TUNING SECTION
# ============================================================

MAX_TOP_LEVELS = 6

# Regex fallback rules. These are used after category/craftingStation/interactionHooks.
TOP_CATEGORY_RULES: list[tuple[TopCategoryName, list[str]]] = [
    ("Walls", [
        r"\bwall\b",
        r"\bhalf wall\b",
        r"\bquarter wall\b",
        r"\barched wall\b",
        r"\bdecorative wall\b",
        r"\bdivider\b",
        r"\bwindow\b",
        r"\bgate\b",
        r"\bdoor\b",
    ]),
    ("Floors", [
        r"\bfloor\b",
        r"\bflooring\b",
        r"\bcarpet\b",
        r"\brug\b",
        r"\bcurtain\b",
        r"\bdrapes\b",
    ]),
    ("Roofs", [
        r"\broof\b",
        r"\bridge\b",
        r"\binner corner\b",
        r"\bouter corner\b",
        r"\b26°\b",
        r"\b45°\b",
        r"\binverted\b",
        r"\bupsidedown\b",
    ]),
    ("Beams", [
        r"\bbeam\b",
        r"\bpole\b",
        r"\bpillar\b",
        r"\bcolumn\b",
        r"\barch\b",
        r"\bcross\b",
        r"\bspire\b",
        r"\bplinth\b",
        r"\bcornice\b",
    ]),
    ("Stairs", [
        r"\bstair\b",
        r"\bstairs\b",
        r"\bstaircase\b",
        r"\bladder\b",
        r"\bspiral stair\b",
    ]),
    ("Stations", [
        r"\bforge\b",
        r"\bartisan\b",
        r"\btable\b",
        r"\bcauldron\b",
        r"\bkiln\b",
        r"\bfurnace\b",
        r"\brefinery\b",
        r"\bworkbench\b",
        r"\bpress\b",
        r"\bvice\b",
        r"\bcutter\b",
        r"\bcartography\b",
        r"\bbarber\b",
        r"\bobliterator\b",
        r"\bfermenter\b",
    ]),
    ("Furniture", [
        r"\bbed\b",
        r"\bbench\b",
        r"\bchair\b",
        r"\bstool\b",
        r"\bthrone\b",
        r"\bstand\b",
        r"\bchest\b",
        r"\bbarrel\b",
        r"\btarget\b",
        r"\bhot tub\b",
    ]),
    ("Lighting", [
        r"\blantern\b",
        r"\bbrazier\b",
        r"\bcandle\b",
        r"\bfire\b",
        r"\bcampfire\b",
        r"\bbonfire\b",
        r"\bhearth\b",
        r"\blight\b",
        r"\bsconce\b",
    ]),
    ("Utility", [
        r"\bstack\b",
        r"\bpile\b",
        r"\bram\b",
        r"\bcatapult\b",
        r"\bstake\b",
        r"\bbeehive\b",
        r"\bcooking station\b",
        r"\bward\b",
        r"\bcart\b",
        r"\bkarve\b",
    ]),
    ("Decor", [
        r"\bbanner\b",
        r"\badornment\b",
        r"\bgarland\b",
        r"\bskeleton\b",
        r"\braven\b",
        r"\bwolf\b",
    ]),
]

# Input category mapping is now the first major signal.
SOURCE_CATEGORY_TO_TOP: dict[SourceCategoryName, TopCategoryName] = {
    "Furniture": "Furniture",
    "Crafting": "Stations",
    "BuildingWorkbench": "Building",
    "BuildingStonecutter": "Building",
    "Misc": "Misc",
}

# Second signal: crafting station.
CRAFTING_STATION_TO_TOP: dict[CraftingStationName, TopCategoryName] = {
    "Workbench": "Building",
    "Stonecutter": "Building",
    "Forge": "Stations",
    "Black Forge": "Stations",
    "Artisan Table": "Stations",
}

# Third signal: interaction hooks.
INTERACTION_HOOK_TO_TOP: dict[InteractionHookName, TopCategoryName] = {
    "CraftingStation": "Stations",
    "StationExtension": "Stations",
    "Smelter": "Stations",
    "Fermenter": "Stations",
    "Bed": "Furniture",
    "Door": "Walls",
    "Fireplace": "Lighting",
    "PrivateArea": "Utility",
    "Vagon": "Utility",
}

# When source category / crafting station collapses to "Building", refine by name/prefab.
BUILDING_REFINEMENT_RULES: list[tuple[TopCategoryName, list[str]]] = [
    ("Walls", [
        r"\bwall\b",
        r"\bhalf wall\b",
        r"\bquarter wall\b",
        r"\barched wall\b",
        r"\bdecorative wall\b",
        r"\bdivider\b",
        r"\bwindow\b",
        r"\bgate\b",
        r"\bdoor\b",
    ]),
    ("Floors", [
        r"\bfloor\b",
        r"\bfloor triangle\b",
        r"\bcarpet\b",
        r"\brug\b",
        r"\bcurtain\b",
        r"\bdrapes\b",
    ]),
    ("Roofs", [
        r"\broof\b",
        r"\bridge\b",
        r"\binner corner\b",
        r"\bouter corner\b",
        r"\b26°\b",
        r"\b45°\b",
        r"\binverted\b",
        r"\bupsidedown\b",
    ]),
    ("Stairs", [
        r"\bstair\b",
        r"\bstairs\b",
        r"\bstaircase\b",
        r"\bspiral stair\b",
    ]),
    ("Beams", [
        r"\bbeam\b",
        r"\bpole\b",
        r"\bpillar\b",
        r"\bcolumn\b",
        r"\barch\b",
        r"\bcross\b",
        r"\bspire\b",
        r"\bplinth\b",
        r"\bcornice\b",
    ]),
]

KNOWN_MATERIAL_ALIASES: dict[str, MaterialName] = {
    "iron nails": "Iron",
    "bronze nails": "Bronze",
    "fine wood": "Finewood",
    "blackmarble": "Black Marble",
    "black metal": "Black Metal",
    "yggdrasilwood": "Yggdrasil Wood",
}

STRUCTURAL_MATERIAL_SCORES: dict[MaterialName, int] = {
    "Wood": 100,
    "Finewood": 95,
    "Corewood": 95,
    "Ashwood": 100,
    "Yggdrasil Wood": 100,
    "Stone": 100,
    "Black Marble": 100,
    "Grausten": 100,
    "Crystal": 90,
    "Iron": 80,
    "Copper": 75,
    "Bronze": 75,
    "Black Metal": 75,
    "Flametal": 85,
    "Red Jute": 60,
    "Blue Jute": 60,
    "Bone Fragments": 55,
}

DEFAULT_UNKNOWN_MATERIAL_SCORE = 15

PREFAB_TOP_OVERRIDES: dict[PrefabName, TopCategoryName] = {
    # "piece_blackmarble_bench": "Furniture",
}

NAME_TOP_OVERRIDES: dict[PieceName, TopCategoryName] = {
    # "Black Marble Bench": "Furniture",
}


# ============================================================
# HELPERS
# ============================================================

def clean_text(text: str) -> str:
    """
    Normalize general text by trimming leading/trailing whitespace and collapsing
    internal whitespace to single spaces.

    Parameters:
        text (str): Raw text value.

    Returns:
        str: Cleaned text.
    """
    return " ".join(str(text).strip().split())


def loose_text(text: str) -> str:
    """
    Loosely normalize text for matching and material parsing.

    Parameters:
        text (str): Raw text value.

    Returns:
        str: Lower-entropy version of the text with underscores/hyphens replaced
        and whitespace normalized.
    """
    text = str(text).strip().replace("_", " ").replace("-", " ")
    text = re.sub(r"\s+", " ", text)
    return text


def titleize_material(text: str) -> MaterialName:
    """
    Convert a material-like string into a title-cased display form.

    Parameters:
        text (str): Raw material text.

    Returns:
        MaterialName: Title-cased material string.
    """
    return " ".join(word.capitalize() for word in loose_text(text).split())


def normalize_material(raw: str) -> MaterialName:
    """
    Normalize a material name into a canonical bucket form.

    Parameters:
        raw (str): Raw material name from the input file.

    Returns:
        MaterialName: Canonicalized material name.
    """
    cleaned = loose_text(raw)
    lowered = cleaned.lower()
    collapsed = lowered.replace(" ", "")

    if lowered in KNOWN_MATERIAL_ALIASES:
        return KNOWN_MATERIAL_ALIASES[lowered]
    if collapsed in KNOWN_MATERIAL_ALIASES:
        return KNOWN_MATERIAL_ALIASES[collapsed]

    return titleize_material(cleaned)


def extract_required_items(required: Any) -> list[RequiredItem]:
    """
    Parse the 'required' field into a normalized list of (material, amount) tuples.

    Supported input formats:
    - [{"amount": 8, "required": "Finewood"}]
    - [{"item": "Wood", "amount": 2}]
    - ["Wood", "Stone"]
    - {"Wood": 2, "Stone": 3}

    Parameters:
        required (Any): Raw required field from a piece.

    Returns:
        list[RequiredItem]: Normalized material requirements.
    """
    out: list[RequiredItem] = []

    if required is None:
        return out

    if isinstance(required, list):
        for entry in required:
            if isinstance(entry, str):
                out.append((normalize_material(entry), 1))
            elif isinstance(entry, dict):
                material = (
                    entry.get("required")
                    or entry.get("item")
                    or entry.get("name")
                    or entry.get("material")
                )
                amount = entry.get("amount", entry.get("count", 1))
                if material:
                    try:
                        amount = int(amount)
                    except (ValueError, TypeError):
                        amount = 1
                    out.append((normalize_material(material), amount))

    elif isinstance(required, dict):
        for material, amount in required.items():
            try:
                amount = int(amount)
            except (ValueError, TypeError):
                amount = 1
            out.append((normalize_material(material), amount))

    return out


def load_pieces(data: RawJsonDocument) -> list[RawPieceRecord]:
    """
    Extract the list of piece records from the loaded JSON document.

    Parameters:
        data (RawJsonDocument): Parsed JSON file contents.

    Returns:
        list[RawPieceRecord]: Raw piece records.

    Raises:
        ValueError: If top-level 'pieces' are missing or not a list.
    """
    pieces = data.get("pieces", [])
    if not isinstance(pieces, list):
        raise ValueError("Expected JSON with top-level key 'pieces' containing a list.")
    return pieces


def normalize_interaction_hooks(value: Any) -> list[InteractionHookName]:
    """
    Normalize interactionHooks into a list of clean strings.

    Parameters:
        value (Any): Raw interactionHooks field.

    Returns:
        list[InteractionHookName]: Clean hook names.
    """
    if not isinstance(value, list):
        return []

    out: list[InteractionHookName] = []
    for item in value:
        if item is None:
            continue
        item_text = clean_text(str(item))
        if item_text:
            out.append(item_text)
    return out


def regex_match_category(
    text: str,
    rules: list[tuple[TopCategoryName, list[str]]],
) -> TopCategoryName | None:
    """
    Try to match a category from regex rules.

    Parameters:
        text (str): Search text, typically combined name/prefab.
        rules (list[tuple[str, list[str]]]): Category-to-pattern mapping.

    Returns:
        TopCategoryName | None: Matched category if found, else None.
    """
    lowered = text.lower()
    for category, patterns in rules:
        for pattern in patterns:
            if re.search(pattern, lowered):
                return category
    return None


# ============================================================
# CLASSIFICATION
# ============================================================

def classify_top_category(
    name: PieceName,
    prefab: PrefabName,
    source_category: SourceCategoryName,
    crafting_station: CraftingStationName,
    interaction_hooks: list[InteractionHookName],
) -> tuple[TopCategoryName, list[str]]:
    """
    Determine the top-level matrix category for a piece.

    Priority order:
    1. explicit prefab override
    2. explicit name override
    3. source input 'category'
    4. source input 'craftingStation'
    5. source input 'interactionHooks'
    6. name/prefab regex rules

    Special handling:
    - If the source category or crafting station resolves to "Building", then the
      piece is further refined into Walls/Floors/Roofs/Beams/Stairs using
      building-specific regex rules.

    Parameters:
        name (PieceName): Piece display name.
        prefab (PrefabName): Piece prefab identifier.
        source_category (SourceCategoryName): Input category field.
        crafting_station (CraftingStationName): Input craftingStation field.
        interaction_hooks (list[InteractionHookName]): Input interactionHooks field.

    Returns:
        tuple[TopCategoryName, list[str]]:
            - selected top-level category
            - reasons explaining the decision
    """
    reasons: list[str] = []
    combined_text = f"{name} {prefab}"

    if prefab in PREFAB_TOP_OVERRIDES:
        category = PREFAB_TOP_OVERRIDES[prefab]
        reasons.append(f"prefab override -> {category}")
        return category, reasons

    if name in NAME_TOP_OVERRIDES:
        category = NAME_TOP_OVERRIDES[name]
        reasons.append(f"name override -> {category}")
        return category, reasons

    mapped_source = SOURCE_CATEGORY_TO_TOP.get(source_category)
    if mapped_source:
        reasons.append(f"source category '{source_category}' -> {mapped_source}")
        if mapped_source == "Building":
            refined = regex_match_category(combined_text, BUILDING_REFINEMENT_RULES)
            if refined:
                reasons.append(f"building refinement by name/prefab -> {refined}")
                return refined, reasons
            reasons.append("building refinement found no match -> Building")
            return "Building", reasons
        return mapped_source, reasons

    mapped_station = CRAFTING_STATION_TO_TOP.get(crafting_station)
    if mapped_station:
        reasons.append(f"crafting station '{crafting_station}' -> {mapped_station}")
        if mapped_station == "Building":
            refined = regex_match_category(combined_text, BUILDING_REFINEMENT_RULES)
            if refined:
                reasons.append(f"building refinement by name/prefab -> {refined}")
                return refined, reasons
            reasons.append("building refinement found no match -> Building")
            return "Building", reasons
        return mapped_station, reasons

    for hook in interaction_hooks:
        mapped_hook = INTERACTION_HOOK_TO_TOP.get(hook)
        if mapped_hook:
            reasons.append(f"interaction hook '{hook}' -> {mapped_hook}")
            return mapped_hook, reasons

    matched = regex_match_category(combined_text, TOP_CATEGORY_RULES)
    if matched:
        reasons.append(f"name/prefab regex -> {matched}")
        return matched, reasons

    reasons.append("no rule matched -> Misc")
    return "Misc", reasons


def choose_subcategory(
    name: PieceName,
    prefab: PrefabName,
    required_items: list[RequiredItem],
    global_material_presence: MaterialCounter,
) -> tuple[SubCategoryName, list[str]]:
    """
    Choose the most likely material subcategory for a piece.

    Scoring factors:
    - material amount within the piece recipe
    - structural material priority
    - dataset-wide material presence
    - material text appearing in name/prefab

    Parameters:
        name (PieceName): Piece display name.
        prefab (PrefabName): Piece prefab identifier.
        required_items (list[RequiredItem]): Material requirements.
        global_material_presence (MaterialCounter): Number of pieces that use each material.

    Returns:
        tuple[SubCategoryName, list[str]]:
            - chosen subcategory material
            - scoring reasons
    """
    reasons: list[str] = []

    if not required_items:
        reasons.append("no required items -> Unknown")
        return "Unknown", reasons

    per_piece: CounterType[MaterialName] = Counter()
    for material, amount in required_items:
        per_piece[material] += amount

    lowered_text = f"{name} {prefab}".lower()

    best_material: MaterialName | None = None
    best_score: int | None = None

    for material, amount in per_piece.items():
        structural = STRUCTURAL_MATERIAL_SCORES.get(material, DEFAULT_UNKNOWN_MATERIAL_SCORE)
        presence = global_material_presence[material]
        name_hint = 100 if material.lower() in lowered_text else 0

        score = (
            amount * 1000
            + structural * 10
            + presence * 2
            + name_hint
        )

        reasons.append(
            f"{material}: amount={amount}, structural={structural}, "
            f"presence={presence}, name_hint={name_hint}, total={score}"
        )

        if best_score is None or score > best_score:
            best_score = score
            best_material = material

    reasons.append(f"selected -> {best_material}")
    return best_material or "Unknown", reasons


def merge_top_categories(
    grouped: Matrix,
    max_top_levels: int,
) -> Matrix:
    """
    Enforce the maximum number of top-level categories by keeping the largest
    categories and merging the rest into Misc.

    Parameters:
        grouped (Matrix): Unmerged matrix structure.
        max_top_levels (int): Maximum number of top-level categories allowed.

    Returns:
        Matrix: Possibly merged matrix.
    """
    if len(grouped) <= max_top_levels:
        return grouped

    counts = {
        top: sum(len(records) for records in submap.values())
        for top, submap in grouped.items()
    }

    keep = set(
        top for top, _ in sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))[:max_top_levels]
    )

    if "Misc" not in keep:
        smallest = sorted(keep, key=lambda k: (counts[k], k))[0]
        keep.remove(smallest)
        keep.add("Misc")

    merged: dict[TopCategoryName, dict[SubCategoryName, list[MatrixRecord]]] = defaultdict(
        lambda: defaultdict(list)
    )

    for top, submap in grouped.items():
        dest_top = top if top in keep else "Misc"
        for sub, records in submap.items():
            merged[dest_top][sub].extend(records)

    return {top: dict(submap) for top, submap in merged.items()}


# ============================================================
# DIAGNOSTICS
# ============================================================

def collect_observed_materials(raw_pieces: list[RawPieceRecord]) -> list[MaterialName]:
    """
    Collect all unique normalized materials observed in the input.

    Parameters:
        raw_pieces (list[RawPieceRecord]): Raw piece records.

    Returns:
        list[MaterialName]: Sorted unique material names.
    """
    seen: set[MaterialName] = set()
    for piece in raw_pieces:
        for material, _amount in extract_required_items(piece.get("required", [])):
            seen.add(material)
    return sorted(seen)


def find_possible_alias_collisions(
    materials: list[MaterialName],
) -> dict[str, list[MaterialName]]:
    """
    Detect likely duplicate material names caused by spacing or punctuation differences.

    Parameters:
        materials (list[MaterialName]): Material names to inspect.

    Returns:
        dict[str, list[MaterialName]]:
            Collision groups keyed by simplified canonical form.
    """
    buckets: dict[str, list[MaterialName]] = defaultdict(list)
    for material in materials:
        key = re.sub(r"[^a-z0-9]", "", material.lower())
        buckets[key].append(material)

    collisions: dict[str, list[MaterialName]] = {}
    for key, vals in buckets.items():
        uniq = sorted(set(vals))
        if len(uniq) > 1:
            collisions[key] = uniq
    return collisions


def summarize_matrix(matrix: Matrix) -> dict[str, Any]:
    """
    Create a count-only summary of the matrix.

    Parameters:
        matrix (Matrix): Full matrix.

    Returns:
        dict[str, Any]: Summary counts by top category and subcategory.
    """
    return {
        top: {sub: len(records) for sub, records in submap.items()}
        for top, submap in matrix.items()
    }


# ============================================================
# MAIN PIPELINE
# ============================================================

def build_matrix_and_diagnostics(data: RawJsonDocument) -> tuple[Matrix, Diagnostics]:
    """
    Build the final matrix and diagnostics structure from the raw JSON input.

    Expected input record fields now include:
    - Prefab or prefab
    - name
    - token
    - category
    - craftingStation
    - interactionHooks
    - required

    Parameters:
        data (RawJsonDocument): Parsed JSON data.

    Returns:
        tuple[Matrix, Diagnostics]:
            - matrix: categorized output
            - diagnostics: tuning/debug information
    """
    raw_pieces = load_pieces(data)

    normalized_records: list[NormalizedPieceRecord] = []
    global_material_presence: MaterialCounter = Counter()

    for piece in raw_pieces:
        name: PieceName = clean_text(piece.get("name", ""))
        prefab: PrefabName = clean_text(piece.get("Prefab", piece.get("prefab", "")))
        token: TokenString = clean_text(piece.get("token", ""))
        source_category: SourceCategoryName = clean_text(piece.get("category", ""))
        crafting_station: CraftingStationName = clean_text(piece.get("craftingStation", ""))
        interaction_hooks: list[InteractionHookName] = normalize_interaction_hooks(
            piece.get("interactionHooks", [])
        )
        required_items: list[RequiredItem] = extract_required_items(piece.get("required", []))

        normalized_records.append({
            "name": name,
            "prefab": prefab,
            "token": token,
            "source_category": source_category,
            "crafting_station": crafting_station,
            "interaction_hooks": interaction_hooks,
            "required_items": required_items,
        })

        seen_materials = {material for material, _ in required_items}
        global_material_presence.update(seen_materials)

    grouped: Matrix = defaultdict(lambda: defaultdict(list))
    record_diagnostics: list[DiagnosticsRecord] = []

    for record in normalized_records:
        name: PieceName = record["name"]
        prefab: PrefabName = record["prefab"]
        token: TokenString = record["token"]
        source_category: SourceCategoryName = record["source_category"]
        crafting_station: CraftingStationName = record["crafting_station"]
        interaction_hooks: list[InteractionHookName] = record["interaction_hooks"]
        required_items: list[RequiredItem] = record["required_items"]

        top_category, top_reasons = classify_top_category(
            name=name,
            prefab=prefab,
            source_category=source_category,
            crafting_station=crafting_station,
            interaction_hooks=interaction_hooks,
        )
        subcategory, sub_reasons = choose_subcategory(
            name=name,
            prefab=prefab,
            required_items=required_items,
            global_material_presence=global_material_presence,
        )

        grouped[top_category][subcategory].append({
            "name": name,
            "prefab": prefab,
            "token": token,
            "category": source_category,
            "craftingStation": crafting_station,
            "interactionHooks": interaction_hooks,
            "required": [
                {"item": material, "amount": amount}
                for material, amount in required_items
            ],
        })

        record_diagnostics.append({
            "name": name,
            "prefab": prefab,
            "source_category": source_category,
            "crafting_station": crafting_station,
            "interaction_hooks": interaction_hooks,
            "top_category": top_category,
            "subcategory": subcategory,
            "top_reasons": top_reasons,
            "sub_reasons": sub_reasons,
        })

    grouped = merge_top_categories(grouped, MAX_TOP_LEVELS)

    top_counts = {
        top: sum(len(records) for records in submap.values())
        for top, submap in grouped.items()
    }

    matrix: Matrix = {}
    for top in sorted(grouped.keys(), key=lambda t: (-top_counts[t], t)):
        submap = grouped[top]
        sub_counts = {sub: len(records) for sub, records in submap.items()}

        matrix[top] = {}
        for sub in sorted(submap.keys(), key=lambda s: (-sub_counts[s], s)):
            matrix[top][sub] = sorted(
                submap[sub],
                key=lambda r: (r["name"].lower(), r["prefab"].lower())
            )

    observed_materials = collect_observed_materials(raw_pieces)

    diagnostics: Diagnostics = {
        "summary": summarize_matrix(matrix),
        "observed_materials": observed_materials,
        "material_presence_counts": dict(sorted(global_material_presence.items())),
        "possible_alias_collisions": find_possible_alias_collisions(observed_materials),
        "record_diagnostics": record_diagnostics,
    }

    return matrix, diagnostics


def print_console_summary(matrix: Matrix, diagnostics: Diagnostics) -> None:
    """
    Print a readable summary of the matrix and any detected material alias collisions.

    Parameters:
        matrix (Matrix): Final matrix output.
        diagnostics (Diagnostics): Diagnostics output.

    Returns:
        None
    """
    print("\nMatrix summary")
    print("=" * 60)
    for top, submap in matrix.items():
        total = sum(len(records) for records in submap.values())
        print(f"{top} ({total})")
        for sub, records in submap.items():
            print(f"  - {sub}: {len(records)}")
        print()

    collisions = diagnostics.get("possible_alias_collisions", {})
    if collisions:
        print("Possible alias collisions")
        print("=" * 60)
        for key, values in collisions.items():
            print(f"{key}: {values}")
        print()


def main() -> None:
    """
    Command-line entry point.

    Usage:
        python build_piece_matrix.py input.json
        python build_piece_matrix.py input.json output_matrix.json

    Returns:
        None

    Side effects:
        - reads input JSON
        - writes matrix JSON
        - writes diagnostics JSON
        - prints console summary
    """
    if len(sys.argv) < 2:
        print("Usage: python build_piece_matrix.py input.json [output_matrix.json]")
        sys.exit(1)

    input_path = Path(sys.argv[1])

    if len(sys.argv) >= 3:
        matrix_output_path = Path(sys.argv[2])
    else:
        matrix_output_path = input_path.with_name(f"{input_path.stem} - matrix.json")

    diagnostics_output_path = matrix_output_path.with_name(
        f"{matrix_output_path.stem} - diagnostics.json"
    )

    with input_path.open("r", encoding="utf-8") as f:
        data: RawJsonDocument = json.load(f)

    matrix, diagnostics = build_matrix_and_diagnostics(data)

    with matrix_output_path.open("w", encoding="utf-8") as f:
        json.dump(matrix, f, indent=2, ensure_ascii=False)

    with diagnostics_output_path.open("w", encoding="utf-8") as f:
        json.dump(diagnostics, f, indent=2, ensure_ascii=False)

    print_console_summary(matrix, diagnostics)
    print(f"Wrote matrix:      {matrix_output_path}")
    print(f"Wrote diagnostics: {diagnostics_output_path}")


if __name__ == "__main__":
    main()