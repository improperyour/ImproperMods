#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
import re
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any, Counter as CounterType, TypeAlias

"""
How items are determined to be sorted

If you want an item (specific or regex) to go to a specific Top/Sub tree, use:

    PLACEMENT_RULES
        property   = <prefab|name|combined|category|crafting_station|interaction_hooks|system_effects>
        match_type = <exact|regex>
        match      = literal or regex string
        top        = top level category (required)
        subcategory= secondary level category (optional)
        
How an Item is sorted:

    1. First thing an item goes through is PLACEMENT_RULES.
        If a rule matches, top is forced immediately, and subcategory is forced when provided.
        property searched: various
        ex:
        {
          "property": "<string>",
          "match_type": "<string>",
          "patterns": [
            "<string>" or "<regex-string>"
          ],
          "top": "<string>",
          "subcategory": "<string>"
        },
 
    2. SOURCE_CATEGORY_TO_TOP (category field)
        If a category is assigned to it from the game, it will choose that.
        This is how the game normally sorts items in the menus.  It's not terribly reliable.
        searches: category
        ex:
            "<string>": "<string>"
            
    3. Top category will be hard set to "Unknown"
            
Now, if any piece hasn't had their top or subcategory defined yet, the following rules will be checked.
If the top is not defined, it potentially can be found here.
If after this the top is still not defined, it will be set to "Unknown"
        
    1. SUBCATEGORY_OVERRIDES
        Ordered property-aware regex overrides for subcategory.
        property searched: prefab or name
        ex:
              {
                    "property": "<prefab|name>",
                    "pattern": "<regex string>",
                    "top": "<optional string>",
                    "subcategory": "<string>"
              },
            
    2. material_precedence

"""

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
SplitMatrix: TypeAlias = dict[str, Matrix]

DiagnosticsRecord: TypeAlias = dict[str, Any]
Diagnostics: TypeAlias = dict[str, Any]

MaterialCounter: TypeAlias = CounterType[MaterialName]


# ============================================================
# TUNING SECTION
# ============================================================

DEFAULT_CONFIG_VERSION = "1.0"
DEFAULT_CONFIG_FILENAME = "BuildPieceMatrixConfig.json"

# Values are loaded from external config in main().
MAX_TOP_LEVELS = 8
MAX_SUBCATEGORIES_PER_TOP = 10
MIN_SUBCATEGORY_SIZE = 2
OTHER_SUBCATEGORY_NAME = "Other"
MATERIAL_PRECEDENCE: list[MaterialName] = []
PLACEMENT_RULES: list[
    tuple[str, str, list[str], TopCategoryName, SubCategoryName | None]
] = []
SUBCATEGORY_OVERRIDES: list[tuple[str, str, TopCategoryName | None, SubCategoryName]] = []
SOURCE_CATEGORY_TO_TOP: dict[SourceCategoryName, TopCategoryName] = {}
KNOWN_MATERIAL_ALIASES: dict[str, MaterialName] = {}
MATERIAL_FAMILY_NORMALIZATION: dict[MaterialName, MaterialName] = {}
STRUCTURAL_MATERIAL_SCORES: dict[MaterialName, int] = {}
DEFAULT_UNKNOWN_MATERIAL_SCORE = 15
SECONDARY_STRUCTURAL_SCORE_THRESHOLD = 70
SECONDARY_STRUCTURAL_RATIO_THRESHOLD = 0.35
AUXILIARY_MATERIALS: set[MaterialName] = set()


def _require_keys(obj: dict[str, Any], keys: list[str], section: str) -> None:
    missing = [k for k in keys if k not in obj]
    if missing:
        raise ValueError(f"Missing required keys in '{section}': {missing}")


def _validate_regex(pattern: str, section: str) -> None:
    try:
        re.compile(pattern)
    except re.error as exc:
        raise ValueError(f"Invalid regex in '{section}': {pattern!r} ({exc})") from exc


def _strip_comment_keys(value: Any) -> Any:
    """
    Recursively remove config keys named "_comment".

    Parameters:
        value (Any): Raw parsed JSON value.

    Returns:
        Any: Value with comment keys removed.
    """
    if isinstance(value, dict):
        out: dict[str, Any] = {}
        for k, v in value.items():
            if k == "_comment":
                continue
            out[k] = _strip_comment_keys(v)
        return out
    if isinstance(value, list):
        out_list: list[Any] = []
        for item in value:
            stripped_item = _strip_comment_keys(item)
            if isinstance(stripped_item, dict) and not stripped_item:
                continue
            out_list.append(stripped_item)
        return out_list
    return value


def _extract_rules_list(container: Any, section: str) -> list[Any]:
    """
    Extract a rules list from a config section shaped as:
      {"_comment": "...", "rules": [ ... ]}
    """
    if not isinstance(container, dict):
        raise ValueError(f"'{section}' must be an object containing a 'rules' array.")
    _require_keys(container, ["rules"], section)
    rules = container["rules"]
    if not isinstance(rules, list):
        raise ValueError(f"'{section}.rules' must be an array.")
    return rules


def _parse_placement_rules(
    overrides: dict[str, Any]
) -> list[tuple[str, str, list[str], TopCategoryName, SubCategoryName | None]]:
    """
    Parse full placement overrides.

    Each rule supports:
      - property: one of:
          "prefab", "name", "combined", "category", "crafting_station",
          "interaction_hooks", "system_effects"
      - match_type: "exact" | "regex"
      - patterns: list of literals or regex patterns (based on match_type)
      - top: required target top category
      - subcategory: optional target subcategory
    """
    out: list[tuple[str, str, list[str], TopCategoryName, SubCategoryName | None]] = []
    rules = _extract_rules_list(overrides["placement_rules"], "overrides.placement_rules")
    for idx, entry in enumerate(rules):
        rule_section = f"overrides.placement_rules[{idx}]"
        if not isinstance(entry, dict):
            raise ValueError(f"Entry in '{rule_section}' must be an object. Record: {entry!r}")

        missing = [k for k in ["property", "match_type", "patterns", "top"] if k not in entry]
        if missing:
            record_dump = json.dumps(entry, ensure_ascii=False, sort_keys=True)
            raise ValueError(
                f"Missing required keys in '{rule_section}': {missing}. Record: {record_dump}"
            )
        property_name = str(entry["property"]).strip().lower()
        if property_name not in {
            "prefab",
            "name",
            "combined",
            "category",
            "crafting_station",
            "interaction_hooks",
            "system_effects",
        }:
            raise ValueError(
                f"Invalid property in '{rule_section}'. "
                "Expected one of: prefab, name, combined, category, "
                "crafting_station, interaction_hooks, system_effects."
            )

        match_type = str(entry["match_type"]).strip().lower()
        if match_type not in {"exact", "regex"}:
            raise ValueError(
                f"Invalid match_type in '{rule_section}'. "
                "Expected one of: exact, regex."
            )

        patterns_raw = entry["patterns"]
        if not isinstance(patterns_raw, list) or not patterns_raw:
            raise ValueError(
                f"Invalid patterns in '{rule_section}'. "
                "Expected a non-empty array."
            )
        patterns = [str(x) for x in patterns_raw]
        if match_type == "regex":
            for pattern in patterns:
                _validate_regex(pattern, rule_section)

        top = str(entry["top"])
        subcategory_raw = entry.get("subcategory")
        subcategory = None if subcategory_raw is None else str(subcategory_raw)
        out.append((property_name, match_type, patterns, top, subcategory))

    return out


def _parse_subcategory_overrides(
    overrides: dict[str, Any]
) -> list[tuple[str, str, TopCategoryName | None, SubCategoryName]]:
    """
    Parse unified subcategory overrides.

    Each rule supports:
      - property: "prefab" | "name"
      - pattern: regex
      - subcategory: target subcategory
      - top: optional top-category filter
    """
    out: list[tuple[str, str, TopCategoryName | None, SubCategoryName]] = []
    rules = _extract_rules_list(overrides["subcategory_overrides"], "overrides.subcategory_overrides")
    for entry in rules:
        if not isinstance(entry, dict):
            raise ValueError("Entries in 'overrides.subcategory_overrides' must be objects.")

        _require_keys(
            entry,
            ["property", "pattern", "subcategory"],
            "overrides.subcategory_overrides",
        )
        property_name = str(entry["property"]).strip().lower()
        if property_name not in {"prefab", "name"}:
            raise ValueError(
                "Invalid property in 'overrides.subcategory_overrides'. "
                "Expected one of: prefab, name."
            )

        pattern = str(entry["pattern"])
        _validate_regex(pattern, "overrides.subcategory_overrides")
        top_filter = entry.get("top")
        subcategory = str(entry["subcategory"])
        out.append(
            (
                property_name,
                pattern,
                None if top_filter is None else str(top_filter),
                subcategory,
            )
        )
    return out


def load_config(config_path: Path) -> RawJsonDocument:
    if not config_path.exists():
        raise FileNotFoundError(f"Config file not found: {config_path}")

    with config_path.open("r", encoding="utf-8") as f:
        config_raw: RawJsonDocument = json.load(f)
    config: RawJsonDocument = _strip_comment_keys(config_raw)

    version = str(config.get("config_version", "")).strip()
    if version != DEFAULT_CONFIG_VERSION:
        raise ValueError(
            f"Unsupported config_version '{version}'. Expected '{DEFAULT_CONFIG_VERSION}'."
        )

    _require_keys(config, ["limits", "materials", "classification", "overrides", "split"], "root")
    return config


def apply_config(config: RawJsonDocument) -> None:
    global MAX_TOP_LEVELS
    global MAX_SUBCATEGORIES_PER_TOP
    global MIN_SUBCATEGORY_SIZE
    global OTHER_SUBCATEGORY_NAME
    global MATERIAL_PRECEDENCE
    global PLACEMENT_RULES
    global SUBCATEGORY_OVERRIDES
    global SOURCE_CATEGORY_TO_TOP
    global KNOWN_MATERIAL_ALIASES
    global MATERIAL_FAMILY_NORMALIZATION
    global STRUCTURAL_MATERIAL_SCORES
    global DEFAULT_UNKNOWN_MATERIAL_SCORE
    global SECONDARY_STRUCTURAL_SCORE_THRESHOLD
    global SECONDARY_STRUCTURAL_RATIO_THRESHOLD
    global AUXILIARY_MATERIALS

    limits = config["limits"]
    materials = config["materials"]
    classification = config["classification"]
    overrides = config["overrides"]
    split = config["split"]

    _require_keys(
        limits,
        ["max_top_levels", "max_subcategories_per_top", "min_subcategory_size", "other_subcategory_name"],
        "limits",
    )
    _require_keys(
        materials,
        [
            "material_precedence",
            "known_material_aliases",
            "material_family_normalization",
            "structural_material_scores",
            "default_unknown_material_score",
        ],
        "materials",
    )
    _require_keys(
        classification,
        [
            "source_category_to_top",
        ],
        "classification",
    )
    _require_keys(
        overrides,
        [
            "placement_rules",
            "subcategory_overrides",
        ],
        "overrides",
    )
    _require_keys(
        split,
        [
            "secondary_structural_score_threshold",
            "secondary_structural_ratio_threshold",
            "auxiliary_materials",
        ],
        "split",
    )

    MAX_TOP_LEVELS = int(limits["max_top_levels"])
    MAX_SUBCATEGORIES_PER_TOP = int(limits["max_subcategories_per_top"])
    MIN_SUBCATEGORY_SIZE = int(limits["min_subcategory_size"])
    OTHER_SUBCATEGORY_NAME = str(limits["other_subcategory_name"])

    MATERIAL_PRECEDENCE = [
        str(x)
        for x in _extract_rules_list(materials["material_precedence"], "materials.material_precedence")
    ]
    KNOWN_MATERIAL_ALIASES = {
        str(k).lower(): str(v)
        for k, v in dict(materials["known_material_aliases"]).items()
    }
    MATERIAL_FAMILY_NORMALIZATION = {
        str(k): str(v)
        for k, v in dict(materials["material_family_normalization"]).items()
    }
    STRUCTURAL_MATERIAL_SCORES = {
        str(k): int(v)
        for k, v in dict(materials["structural_material_scores"]).items()
    }
    DEFAULT_UNKNOWN_MATERIAL_SCORE = int(materials["default_unknown_material_score"])

    SOURCE_CATEGORY_TO_TOP = {
        str(k): str(v)
        for k, v in dict(classification["source_category_to_top"]).items()
    }
    PLACEMENT_RULES = _parse_placement_rules(overrides)

    SUBCATEGORY_OVERRIDES = _parse_subcategory_overrides(overrides)

    SECONDARY_STRUCTURAL_SCORE_THRESHOLD = int(split["secondary_structural_score_threshold"])
    SECONDARY_STRUCTURAL_RATIO_THRESHOLD = float(split["secondary_structural_ratio_threshold"])
    AUXILIARY_MATERIALS = {str(x) for x in split["auxiliary_materials"]}


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


def normalize_system_effects(value: Any) -> list[str]:
    """
    Normalize systemEffects into a list of clean strings.

    Parameters:
        value (Any): Raw systemEffects field.

    Returns:
        list[str]: Clean system effect strings.
    """
    if not isinstance(value, list):
        return []

    out: list[str] = []
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



def apply_placement_rules(
    name: PieceName,
    prefab: PrefabName,
    source_category: SourceCategoryName,
    crafting_station: CraftingStationName,
    interaction_hooks: list[InteractionHookName],
    system_effects: list[str],
) -> tuple[TopCategoryName | None, SubCategoryName | None, str | None, list[str], int | None]:
    """
    Evaluate full-placement rules that can force top and optionally subcategory.

    Parameters:
        name (PieceName): Piece display name.
        prefab (PrefabName): Piece prefab identifier.
        source_category (SourceCategoryName): Raw source category.
        crafting_station (CraftingStationName): Raw crafting station.
        interaction_hooks (list[InteractionHookName]): Normalized interaction hooks.
        system_effects (list[str]): Normalized system effects.

    Returns:
        tuple[TopCategoryName | None, SubCategoryName | None, str | None, list[str], int | None]:
            - forced top category declared by the matched rule
            - forced subcategory declared by the matched rule
            - reason string for diagnostics
            - reasoning lines for checked placement rules (up to chosen rule)
            - chosen rule index (1-based), if matched
    """
    lowered_name = name.lower()
    lowered_prefab = prefab.lower()
    lowered_source_category = source_category.lower()
    lowered_crafting_station = crafting_station.lower()
    lowered_interaction_hooks = [hook.lower() for hook in interaction_hooks]
    lowered_system_effects = [effect.lower() for effect in system_effects]
    combined_text = f"{lowered_name} {lowered_prefab}"

    top_only_fallback: tuple[
        TopCategoryName | None,
        SubCategoryName | None,
        str | None,
        int,
    ] | None = None
    checked_rule_lines: list[str] = []

    for idx, (property_name, match_type, patterns, forced_top, forced_subcategory) in enumerate(PLACEMENT_RULES, start=1):
        shown = patterns[:3]
        remaining = len(patterns) - len(shown)
        pattern_tokens = list(shown)
        if remaining > 0:
            pattern_tokens.append(f"+{remaining}")
        pattern_list = ",".join(pattern_tokens)
        checked_rule_lines.append(
            f"rule{idx} -> {property_name},{match_type},[{pattern_list}]"
        )

        scalar_targets: dict[str, str] = {
            "name": lowered_name,
            "prefab": lowered_prefab,
            "combined": combined_text,
            "category": lowered_source_category,
            "crafting_station": lowered_crafting_station,
        }
        array_targets: dict[str, list[str]] = {
            "interaction_hooks": lowered_interaction_hooks,
            "system_effects": lowered_system_effects,
        }

        matched = False
        matched_pattern: str | None = None

        if property_name in scalar_targets:
            target = scalar_targets[property_name]
            for pattern in patterns:
                if match_type == "exact":
                    this_match = target == pattern.lower()
                else:
                    this_match = re.search(pattern, target) is not None
                if this_match:
                    matched = True
                    matched_pattern = pattern
                    break
        else:
            targets = array_targets.get(property_name, [])
            for pattern in patterns:
                if match_type == "exact":
                    this_match = any(target == pattern.lower() for target in targets)
                else:
                    this_match = any(re.search(pattern, target) is not None for target in targets)
                if this_match:
                    matched = True
                    matched_pattern = pattern
                    break

        if matched:
            display_target = (
                f"{forced_top}:{forced_subcategory}"
                if forced_subcategory is not None
                else forced_top
            )
            reason = (
                f"placement rule {property_name} {match_type} '{matched_pattern or '<matched>'}'"
                f" -> {display_target}"
            )
            if forced_subcategory is not None:
                checked_rule_lines[-1] = (
                    f"{checked_rule_lines[-1]} -> chosen ({forced_top}:{forced_subcategory})"
                )
                return forced_top, forced_subcategory, reason, checked_rule_lines, idx
            if top_only_fallback is None:
                top_only_fallback = (forced_top, forced_subcategory, reason, idx)

    if top_only_fallback is not None:
        forced_top, forced_subcategory, reason, chosen_idx = top_only_fallback
        out_lines = checked_rule_lines[:chosen_idx]
        if out_lines:
            chosen_target = (
                f"{forced_top}:{forced_subcategory}"
                if forced_subcategory is not None
                else f"{forced_top}"
            )
            out_lines[-1] = f"{out_lines[-1]} -> chosen ({chosen_target})"
        return forced_top, forced_subcategory, reason, out_lines, chosen_idx
    return None, None, None, checked_rule_lines, None


def classify_top_category(
    source_category: SourceCategoryName,
) -> tuple[TopCategoryName, list[str], list[str]]:
    """
    Determine the top-level matrix category for a piece.

    Top-level category (Walls, Crafting, etc.) in build_piece_matrix.py:
    1. SOURCE_CATEGORY_TO_TOP (category field)

    Parameters:
        source_category (SourceCategoryName): Input category field.

    Returns:
        tuple[TopCategoryName, list[str], list[str]]:
            - selected top-level category
            - reasons explaining the decision
            - ordered decision trace
    """
    reasons: list[str] = []
    decision_trace: list[str] = []

    mapped_source = SOURCE_CATEGORY_TO_TOP.get(source_category)
    if mapped_source:
        reasons.append(f"source category '{source_category}' -> {mapped_source}")
        decision_trace.append(
            f"SOURCE_CATEGORY_TO_TOP: chosen (source category '{source_category}' -> {mapped_source})"
        )
        return mapped_source, reasons, decision_trace
    decision_trace.append("SOURCE_CATEGORY_TO_TOP: none")


    reasons.append("no rule matched -> Unknown")
    decision_trace.append("DEFAULT_UNKNOWN: chosen (no rule matched -> Unknown)")
    return "Unknown", reasons, decision_trace


def choose_subcategory(
    name: PieceName,
    prefab: PrefabName,
    top_category: TopCategoryName,
    required_items: list[RequiredItem],
    global_material_presence: MaterialCounter,
) -> tuple[SubCategoryName, list[str], list[str]]:
    """
    Choose the most likely material subcategory for a piece.

    Scoring factors:
    - ordered subcategory overrides
    - explicit material precedence list
    - material amount within the piece recipe
    - structural material priority
    - dataset-wide material presence
    - material text appearing in name/prefab

    Parameters:
        name (PieceName): Piece display name.
        prefab (PrefabName): Piece prefab identifier.
        top_category (TopCategoryName): Selected top-level category.
        required_items (list[RequiredItem]): Material requirements.
        global_material_presence (MaterialCounter): Number of pieces that use each material.

    Returns:
        tuple[SubCategoryName, list[str], list[str]]:
            - chosen subcategory material
            - scoring reasons
            - ordered decision trace
    """
    reasons: list[str] = []
    decision_trace: list[str] = []

    if not required_items:
        reasons.append("no required items -> Unknown")
        decision_trace.append("NO_REQUIRED_ITEMS: chosen (no required items -> Unknown)")
        return "Unknown", reasons, decision_trace
    decision_trace.append("NO_REQUIRED_ITEMS: none")

    per_piece: CounterType[MaterialName] = Counter()
    for material, amount in required_items:
        per_piece[material] += amount

    overridden_subcategory, override_reason = apply_subcategory_override(
        name=name,
        prefab=prefab,
        top_category=top_category,
    )
    if overridden_subcategory:
        reasons.append(override_reason or "subcategory override")
        decision_trace.append(
            "SUBCATEGORY_OVERRIDES: chosen "
            f"({override_reason or 'subcategory override'})"
        )
        return overridden_subcategory, reasons, decision_trace
    decision_trace.append("SUBCATEGORY_OVERRIDES: none")

    if MATERIAL_PRECEDENCE:
        precedence_rank = {material: rank for rank, material in enumerate(MATERIAL_PRECEDENCE)}
        precedence_candidates = [m for m in per_piece.keys() if m in precedence_rank]
        if precedence_candidates:
            chosen = min(precedence_candidates, key=lambda m: precedence_rank[m])
            ordered_candidates = sorted(precedence_candidates, key=lambda m: precedence_rank[m])
            reasons.append(
                "material precedence override -> "
                f"{chosen} (candidates={ordered_candidates})"
            )
            decision_trace.append(
                "MATERIAL_PRECEDENCE: chosen "
                f"(material precedence override -> {chosen}; candidates={ordered_candidates})"
            )
            return chosen, reasons, decision_trace
    decision_trace.append("MATERIAL_PRECEDENCE: none")

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
    decision_trace.append(f"AMOUNT_SCORING: chosen (selected -> {best_material})")
    return best_material or "Unknown", reasons, decision_trace


def normalize_material_family(material: MaterialName) -> MaterialName:
    """
    Normalize a material into a broader family bucket when configured.

    Parameters:
        material (MaterialName): Dominant material selected for a piece.

    Returns:
        MaterialName: Family-normalized material name.
    """
    return MATERIAL_FAMILY_NORMALIZATION.get(material, material)


def apply_subcategory_override(
    name: PieceName,
    prefab: PrefabName,
    top_category: TopCategoryName,
) -> tuple[SubCategoryName | None, str | None]:
    """
    Apply ordered subcategory override rules (property-aware regex).

    Parameters:
        name (PieceName): Piece display name.
        prefab (PrefabName): Piece prefab identifier.
        top_category (TopCategoryName): Already-selected top category.
    Returns:
        tuple[SubCategoryName | None, str | None]:
            - forced subcategory, if any
            - optional reason if an override was applied
    """
    lowered_name = name.lower()
    lowered_prefab = prefab.lower()
    for property_name, pattern, top_filter, forced_subcategory in SUBCATEGORY_OVERRIDES:
        if top_filter is not None and top_category != top_filter:
            continue
        target = lowered_prefab if property_name == "prefab" else lowered_name
        if re.search(pattern, target):
            reason = (
                f"{property_name} subcategory override '{pattern}'"
                f" ({top_category}) -> {forced_subcategory}"
            )
            return forced_subcategory, reason
    return None, None


def classify_exact_vs_contains(
    dominant_material: MaterialName,
    required_items: list[RequiredItem],
) -> tuple[str, list[str]]:
    """
    Split a piece into exact/contains based on whether secondary materials
    are structural (contains) or auxiliary/decorative (exact).

    Parameters:
        dominant_material (MaterialName): Subcategory material selected for the piece.
        required_items (list[RequiredItem]): Material requirements.

    Returns:
        tuple[str, list[str]]:
            - "exact" or "contains"
            - reasons used for diagnostics
    """
    reasons: list[str] = []

    if not required_items:
        reasons.append("no required items -> exact")
        return "exact", reasons

    per_piece: CounterType[MaterialName] = Counter()
    for material, amount in required_items:
        per_piece[material] += amount

    dominant_amount = per_piece.get(dominant_material, 0)
    if dominant_amount <= 0:
        reasons.append("dominant material not found in required -> contains")
        return "contains", reasons

    for material, amount in per_piece.items():
        if material == dominant_material:
            continue

        if material in AUXILIARY_MATERIALS:
            reasons.append(f"{material}: auxiliary allow-list -> ignored")
            continue

        structural_score = STRUCTURAL_MATERIAL_SCORES.get(
            material,
            DEFAULT_UNKNOWN_MATERIAL_SCORE,
        )
        ratio = amount / dominant_amount
        reasons.append(
            f"{material}: structural={structural_score}, ratio={ratio:.3f}"
        )

        if (
            structural_score >= SECONDARY_STRUCTURAL_SCORE_THRESHOLD
            and ratio >= SECONDARY_STRUCTURAL_RATIO_THRESHOLD
        ):
            reasons.append(f"{material}: meaningful secondary structural -> contains")
            return "contains", reasons

    reasons.append("secondary materials are auxiliary/decorative -> exact")
    return "exact", reasons


def merge_top_categories(
    grouped: Matrix,
    max_top_levels: int,
) -> tuple[Matrix, list[str]]:
    """
    Enforce the maximum number of top-level categories by keeping the largest
    categories and merging the rest into Misc.

    Parameters:
        grouped (Matrix): Unmerged matrix structure.
        max_top_levels (int): Maximum number of top-level categories allowed.

    Returns:
        tuple[Matrix, list[str]]:
            - possibly merged matrix
            - warning messages for full-placement overrides displaced by top merge
    """
    warnings: list[str] = []

    if len(grouped) <= max_top_levels:
        return grouped, warnings

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
            if dest_top != top:
                for record in records:
                    if not isinstance(record, dict):
                        continue
                    required_list = record.get("required")
                    has_required = isinstance(required_list, list) and len(required_list) > 0
                    if not has_required:
                        warnings.append(
                            "FILTERED_ITEM_WITHOUT_REQUIRED: "
                            f"{record.get('name', '<unknown>')} ({record.get('prefab', '<unknown>')}) "
                            f"moved_by=MAX_TOP_LEVELS from={top}:{sub} to={dest_top}:{sub}"
                        )
                    target_top = record.get("full_placement_target_top")
                    target_sub = record.get("full_placement_target_subcategory")
                    if target_top is not None and target_sub is not None and target_top == top:
                        warnings.append(
                            "PLACEMENT_RULES displaced by MAX_TOP_LEVELS: "
                            f"{record.get('name', '<unknown>')} ({record.get('prefab', '<unknown>')}) "
                            f"target={target_top}:{target_sub} actual={dest_top}:{record.get('subcategory', sub)}"
                        )
                        reasoning = record.get("reasoning")
                        if isinstance(reasoning, list):
                            reasoning.append(
                                "TOP_CATEGORY_LIMIT: chosen "
                                f"({top} -> {dest_top}; max_top_levels={max_top_levels})"
                            )
            merged[dest_top][sub].extend(records)

    return {top: dict(submap) for top, submap in merged.items()}, warnings


def limit_subcategories_per_top(
    grouped: Matrix,
    max_subcategories_per_top: int,
    min_subcategory_size: int,
    other_name: SubCategoryName = OTHER_SUBCATEGORY_NAME,
) -> tuple[Matrix, list[str]]:
    """
    Limit the number of subcategories under each top-level category.

    Buckets smaller than min_subcategory_size and overflow buckets beyond
    max_subcategories_per_top are merged into a single fallback bucket.

    Parameters:
        grouped (Matrix): Grouped matrix structure.
        max_subcategories_per_top (int): Max buckets to keep per top category.
        min_subcategory_size (int): Minimum size required for a bucket to be kept.
        other_name (SubCategoryName): Fallback bucket name.

    Returns:
        tuple[Matrix, list[str]]:
            - matrix with bounded subcategory fan-out
            - warning messages for full-placement overrides displaced by subcategory cap
    """
    warnings: list[str] = []

    if max_subcategories_per_top <= 0:
        return grouped, warnings

    out: Matrix = {}

    for top, submap in grouped.items():
        counts = {sub: len(records) for sub, records in submap.items()}
        ranked = sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))

        keep_candidates = [
            sub for sub, count in ranked
            if count >= min_subcategory_size and sub != other_name
        ]

        keep_count = min(len(keep_candidates), max_subcategories_per_top)
        provisional_keep = set(keep_candidates[:keep_count])
        overflow_exists = any(sub not in provisional_keep for sub in submap.keys())
        if overflow_exists and keep_count == max_subcategories_per_top and max_subcategories_per_top > 0:
            keep_count -= 1
        keep = set(keep_candidates[:keep_count])

        out[top] = {}
        overflow: list[MatrixRecord] = []

        for sub, records in submap.items():
            if sub in keep:
                out[top][sub] = records
            else:
                for record in records:
                    if not isinstance(record, dict):
                        continue
                    required_list = record.get("required")
                    has_required = isinstance(required_list, list) and len(required_list) > 0
                    if not has_required:
                        warnings.append(
                            "FILTERED_ITEM_WITHOUT_REQUIRED: "
                            f"{record.get('name', '<unknown>')} ({record.get('prefab', '<unknown>')}) "
                            f"moved_by=SUBCATEGORY_LIMIT from={top}:{sub} to={top}:{other_name}"
                        )
                    previous_sub = record.get("subcategory", sub)
                    record["subcategory"] = other_name
                    target_top = record.get("full_placement_target_top")
                    target_sub = record.get("full_placement_target_subcategory")
                    if (
                        target_top is not None
                        and target_sub is not None
                        and target_top == top
                        and target_sub != other_name
                    ):
                        warnings.append(
                            "PLACEMENT_RULES displaced by subcategory cap: "
                            f"{record.get('name', '<unknown>')} ({record.get('prefab', '<unknown>')}) "
                            f"target={target_top}:{target_sub} actual={top}:{other_name} "
                            f"(max={max_subcategories_per_top}, min_size={min_subcategory_size})"
                        )
                    reasoning = record.get("reasoning")
                    if not isinstance(reasoning, list):
                        reasoning = []
                        record["reasoning"] = reasoning
                    reasoning.append(
                        "SUBCATEGORY_LIMIT: chosen "
                        f"({top}:{previous_sub} -> {other_name}; "
                        f"max={max_subcategories_per_top}, min_size={min_subcategory_size})"
                    )
                overflow.extend(records)

        if overflow:
            out[top][other_name] = out[top].get(other_name, []) + overflow

    return out, warnings


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


def summarize_matrix(matrix: SplitMatrix) -> dict[str, Any]:
    """
    Create a count-only summary of the matrix.

    Parameters:
        matrix (SplitMatrix): Full matrix.

    Returns:
        dict[str, Any]: Summary counts by top category and subcategory.
    """
    summary: dict[str, Any] = {}
    for split_key, split_map in matrix.items():
        summary[split_key] = {
            top: {sub: len(records) for sub, records in submap.items()}
            for top, submap in split_map.items()
        }
    return summary


# ============================================================
# MAIN PIPELINE
# ============================================================

def normalize_records_from_raw_pieces(
    raw_pieces: list[RawPieceRecord],
) -> tuple[list[NormalizedPieceRecord], MaterialCounter]:
    """
    Normalize raw piece records and collect per-dataset material presence counts.
    """
    normalized_records: list[NormalizedPieceRecord] = []
    material_presence: MaterialCounter = Counter()

    for piece in raw_pieces:
        name: PieceName = clean_text(piece.get("name", ""))
        prefab: PrefabName = clean_text(piece.get("Prefab", piece.get("prefab", "")))
        source_category: SourceCategoryName = clean_text(piece.get("category", ""))
        crafting_station: CraftingStationName = clean_text(piece.get("craftingStation", ""))
        interaction_hooks: list[InteractionHookName] = normalize_interaction_hooks(
            piece.get("interactionHooks", [])
        )
        system_effects: list[str] = normalize_system_effects(
            piece.get("systemEffects", [])
        )
        required_items: list[RequiredItem] = extract_required_items(piece.get("required", []))

        normalized_records.append({
            "name": name,
            "prefab": prefab,
            "source_category": source_category,
            "crafting_station": crafting_station,
            "interaction_hooks": interaction_hooks,
            "system_effects": system_effects,
            "required_items": required_items,
        })

        seen_materials = {material for material, _ in required_items}
        material_presence.update(seen_materials)

    return normalized_records, material_presence


def classify_records_to_grouped_exact(
    normalized_records: list[NormalizedPieceRecord],
    global_material_presence: MaterialCounter,
) -> Matrix:
    """
    Classify normalized records into the pre-cap exact grouped matrix.
    """
    grouped_exact: Matrix = defaultdict(lambda: defaultdict(list))

    for record in normalized_records:
        name: PieceName = record["name"]
        prefab: PrefabName = record["prefab"]
        source_category: SourceCategoryName = record["source_category"]
        crafting_station: CraftingStationName = record["crafting_station"]
        interaction_hooks: list[InteractionHookName] = record["interaction_hooks"]
        system_effects: list[str] = record["system_effects"]
        required_items: list[RequiredItem] = record["required_items"]

        forced_top, forced_sub, full_placement_reason, placement_rule_reasoning, chosen_rule_idx = apply_placement_rules(
            name=name,
            prefab=prefab,
            source_category=source_category,
            crafting_station=crafting_station,
            interaction_hooks=interaction_hooks,
            system_effects=system_effects,
        )

        reasoning: list[str] = list(placement_rule_reasoning)
        if forced_top is not None:
            top_category = forced_top
            top_reason_text = full_placement_reason or "full placement override"
            if chosen_rule_idx is not None:
                top_reason_text = f"(rule #{chosen_rule_idx}) {top_reason_text}"
            top_reasons = [top_reason_text]
        else:
            top_category, top_reasons, top_trace = classify_top_category(
                source_category=source_category,
            )
        full_applied = False
        if forced_sub is not None:
            subcategory_raw = forced_sub
            sub_reason_text = full_placement_reason or "full placement subcategory override"
            if chosen_rule_idx is not None:
                sub_reason_text = f"(rule #{chosen_rule_idx}) {sub_reason_text}"
            sub_reasons = [sub_reason_text]
            sub_trace = [
                "PLACEMENT_RULES: chosen "
                f"({full_placement_reason or 'placement rule'}; subcategory forced)"
            ]
            full_applied = True

        if not full_applied:
            subcategory_raw, sub_reasons, sub_trace = choose_subcategory(
                name=name,
                prefab=prefab,
                top_category=top_category,
                required_items=required_items,
                global_material_presence=global_material_presence,
            )

        subcategory = subcategory_raw
        subcategory_after_overrides = subcategory
        subcategory = normalize_material_family(subcategory)
        if subcategory != subcategory_after_overrides:
            sub_reasons.append(
                f"family normalization: {subcategory_after_overrides} -> {subcategory}"
            )
        grouped_exact[top_category][subcategory].append({
            "name": name,
            "prefab": prefab,
            "category": source_category,
            "craftingStation": crafting_station,
            "source_category": source_category,
            "interaction_hooks": interaction_hooks,
            "system_effects": system_effects,
            "top_category": top_category,
            "subcategory_raw": subcategory_raw,
            "subcategory": subcategory,
            "reasoning": reasoning,
            "top_reasons": top_reasons,
            "sub_reasons": sub_reasons,
            "full_placement_target_top": forced_top,
            "full_placement_target_subcategory": forced_sub,
            "required": [
                {"required": material, "amount": amount}
                for material, amount in required_items
            ],
        })

    return grouped_exact


def sort_matrix_for_output(grouped_exact: Matrix) -> Matrix:
    combined_top_counts: CounterType[TopCategoryName] = Counter()
    for top, submap in grouped_exact.items():
        combined_top_counts[top] += sum(len(records) for records in submap.values())

    out: Matrix = {}
    for s_top in sorted(grouped_exact.keys(), key=lambda t: (-combined_top_counts[t], t)):
        split_submap = grouped_exact[s_top]
        sub_counts = {sub: len(records) for sub, records in split_submap.items()}

        out[s_top] = {}
        for sub in sorted(split_submap.keys(), key=lambda s: (-sub_counts[s], s)):
            out[s_top][sub] = sorted(
                split_submap[sub],
                key=lambda r: (r["name"].lower(), r["prefab"].lower())
            )
    return out


def build_global_limit_plan(grouped_matrices: list[Matrix]) -> tuple[set[TopCategoryName], dict[TopCategoryName, set[SubCategoryName]]]:
    """
    Build a shared top/subcategory keep plan across multiple pre-cap matrices.
    """
    top_counts: CounterType[TopCategoryName] = Counter()
    for grouped in grouped_matrices:
        for top, submap in grouped.items():
            top_counts[top] += sum(len(records) for records in submap.values())

    all_tops = set(top_counts.keys())
    if len(all_tops) <= MAX_TOP_LEVELS:
        keep_tops = set(all_tops)
    else:
        keep_tops = set(
            top for top, _ in sorted(top_counts.items(), key=lambda kv: (-kv[1], kv[0]))[:MAX_TOP_LEVELS]
        )
        if "Misc" not in keep_tops and keep_tops:
            smallest = sorted(keep_tops, key=lambda k: (top_counts[k], k))[0]
            keep_tops.remove(smallest)
            keep_tops.add("Misc")

    merged_sub_counts: dict[TopCategoryName, CounterType[SubCategoryName]] = defaultdict(Counter)
    for grouped in grouped_matrices:
        for top, submap in grouped.items():
            dest_top = top if top in keep_tops else "Misc"
            for sub, records in submap.items():
                merged_sub_counts[dest_top][sub] += len(records)

    sub_keep: dict[TopCategoryName, set[SubCategoryName]] = {}
    for top, counts in merged_sub_counts.items():
        ranked = sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))
        keep_candidates = [
            sub for sub, count in ranked
            if count >= MIN_SUBCATEGORY_SIZE and sub != OTHER_SUBCATEGORY_NAME
        ]
        keep_count = min(len(keep_candidates), MAX_SUBCATEGORIES_PER_TOP)
        provisional_keep = set(keep_candidates[:keep_count])
        overflow_exists = any(sub not in provisional_keep for sub in counts.keys())
        if overflow_exists and keep_count == MAX_SUBCATEGORIES_PER_TOP and MAX_SUBCATEGORIES_PER_TOP > 0:
            keep_count -= 1
        sub_keep[top] = set(keep_candidates[:keep_count])

    return keep_tops, sub_keep


def apply_global_limit_plan(
    grouped: Matrix,
    keep_tops: set[TopCategoryName],
    sub_keep: dict[TopCategoryName, set[SubCategoryName]],
) -> tuple[Matrix, list[str]]:
    """
    Apply shared top/subcategory keep plan to a single pre-cap matrix.
    """
    warnings: list[str] = []

    merged: dict[TopCategoryName, dict[SubCategoryName, list[MatrixRecord]]] = defaultdict(
        lambda: defaultdict(list)
    )
    for top, submap in grouped.items():
        dest_top = top if top in keep_tops else "Misc"
        for sub, records in submap.items():
            if dest_top != top:
                for record in records:
                    if not isinstance(record, dict):
                        continue
                    required_list = record.get("required")
                    has_required = isinstance(required_list, list) and len(required_list) > 0
                    if not has_required:
                        warnings.append(
                            "FILTERED_ITEM_WITHOUT_REQUIRED: "
                            f"{record.get('name', '<unknown>')} ({record.get('prefab', '<unknown>')}) "
                            f"moved_by=MAX_TOP_LEVELS from={top}:{sub} to={dest_top}:{sub}"
                        )
                    target_top = record.get("full_placement_target_top")
                    target_sub = record.get("full_placement_target_subcategory")
                    if target_top is not None and target_sub is not None and target_top == top:
                        warnings.append(
                            "PLACEMENT_RULES displaced by MAX_TOP_LEVELS: "
                            f"{record.get('name', '<unknown>')} ({record.get('prefab', '<unknown>')}) "
                            f"target={target_top}:{target_sub} actual={dest_top}:{record.get('subcategory', sub)}"
                        )
                        reasoning = record.get("reasoning")
                        if isinstance(reasoning, list):
                            reasoning.append(
                                "TOP_CATEGORY_LIMIT: chosen "
                                f"({top} -> {dest_top}; max_top_levels={MAX_TOP_LEVELS})"
                            )
            merged[dest_top][sub].extend(records)

    out: Matrix = {}
    for top, submap in merged.items():
        keep = sub_keep.get(top, set())
        out[top] = {}
        overflow: list[MatrixRecord] = []
        for sub, records in submap.items():
            if sub in keep:
                out[top][sub] = records
                continue
            for record in records:
                if not isinstance(record, dict):
                    continue
                required_list = record.get("required")
                has_required = isinstance(required_list, list) and len(required_list) > 0
                if not has_required:
                    warnings.append(
                        "FILTERED_ITEM_WITHOUT_REQUIRED: "
                        f"{record.get('name', '<unknown>')} ({record.get('prefab', '<unknown>')}) "
                        f"moved_by=SUBCATEGORY_LIMIT from={top}:{sub} to={top}:{OTHER_SUBCATEGORY_NAME}"
                    )
                previous_sub = record.get("subcategory", sub)
                record["subcategory"] = OTHER_SUBCATEGORY_NAME
                target_top = record.get("full_placement_target_top")
                target_sub = record.get("full_placement_target_subcategory")
                if (
                    target_top is not None
                    and target_sub is not None
                    and target_top == top
                    and target_sub != OTHER_SUBCATEGORY_NAME
                ):
                    warnings.append(
                        "PLACEMENT_RULES displaced by subcategory cap: "
                        f"{record.get('name', '<unknown>')} ({record.get('prefab', '<unknown>')}) "
                        f"target={target_top}:{target_sub} actual={top}:{OTHER_SUBCATEGORY_NAME} "
                        f"(max={MAX_SUBCATEGORIES_PER_TOP}, min_size={MIN_SUBCATEGORY_SIZE})"
                    )
                reasoning = record.get("reasoning")
                if not isinstance(reasoning, list):
                    reasoning = []
                    record["reasoning"] = reasoning
                reasoning.append(
                    "SUBCATEGORY_LIMIT: chosen "
                    f"({top}:{previous_sub} -> {OTHER_SUBCATEGORY_NAME}; "
                    f"max={MAX_SUBCATEGORIES_PER_TOP}, min_size={MIN_SUBCATEGORY_SIZE})"
                )
            overflow.extend(records)
        if overflow:
            out[top][OTHER_SUBCATEGORY_NAME] = out[top].get(OTHER_SUBCATEGORY_NAME, []) + overflow

    return out, warnings


def build_matrix_and_diagnostics(data: RawJsonDocument) -> tuple[SplitMatrix, Diagnostics]:
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
            - matrix: categorized output split into exact/contains
            - diagnostics: tuning/debug information
    """
    raw_pieces = load_pieces(data)

    normalized_records, global_material_presence = normalize_records_from_raw_pieces(raw_pieces)
    grouped_exact = classify_records_to_grouped_exact(normalized_records, global_material_presence)

    grouped_exact, top_limit_warnings = merge_top_categories(grouped_exact, MAX_TOP_LEVELS)
    grouped_exact, sub_limit_warnings = limit_subcategories_per_top(
        grouped_exact,
        max_subcategories_per_top=MAX_SUBCATEGORIES_PER_TOP,
        min_subcategory_size=MIN_SUBCATEGORY_SIZE,
        other_name=OTHER_SUBCATEGORY_NAME,
    )

    matrix: SplitMatrix = {
        "exact": sort_matrix_for_output(grouped_exact),
        "contains": {},
    }

    observed_materials = collect_observed_materials(raw_pieces)
    material_frequency_counts: CounterType[MaterialName] = Counter()
    for record in normalized_records:
        for material, amount in record["required_items"]:
            material_frequency_counts[material] += amount

    diagnostics: Diagnostics = {
        "summary": summarize_matrix(matrix),
        "observed_materials": observed_materials,
        "material_frequency_counts": dict(sorted(material_frequency_counts.items())),
        "material_presence_counts": dict(sorted(global_material_presence.items())),
        "material_prescence_counts": dict(sorted(global_material_presence.items())),
        "possible_alias_collisions": find_possible_alias_collisions(observed_materials),
        "placement_warnings": top_limit_warnings + sub_limit_warnings,
    }

    return matrix, diagnostics


def print_console_summary(matrix: SplitMatrix, diagnostics: Diagnostics) -> None:
    """
    Print a readable summary of the matrix and any detected material alias collisions.

    Parameters:
        matrix (SplitMatrix): Final matrix output.
        diagnostics (Diagnostics): Diagnostics output.

    Returns:
        None
    """
    print("\nMatrix summary")
    print("=" * 60)
    for split_key in ("exact", "contains"):
        split_map = matrix.get(split_key, {})
        split_total = sum(
            len(records)
            for submap in split_map.values()
            for records in submap.values()
        )
        print(f"{split_key} ({split_total})")
        for top, submap in split_map.items():
            total = sum(len(records) for records in submap.values())
            print(f"  {top} ({total})")
            for sub, records in submap.items():
                print(f"    - {sub}: {len(records)}")
        print()

    collisions = diagnostics.get("possible_alias_collisions", {})
    if collisions:
        print("Possible alias collisions")
        print("=" * 60)
        for key, values in collisions.items():
            print(f"{key}: {values}")
        print()

    placement_warnings = diagnostics.get("placement_warnings", [])
    if placement_warnings:
        print("Placement warnings")
        print("=" * 60)
        for warning in placement_warnings:
            print(f"- {warning}")
        print()


def print_file_warnings(file_path: Path, diagnostics: Diagnostics) -> None:
    """
    Print warnings for a processed file in batch mode.
    """
    placement_warnings = diagnostics.get("placement_warnings", [])
    if not placement_warnings:
        return
    print(f"Warnings for {file_path}:")
    for warning in placement_warnings:
        print(f"- {warning}")


def print_short_help() -> None:
    """
    Print concise CLI help.
    """
    print("build_piece_matrix.py")
    print("Build a Valheim piece matrix JSON from an input piece dump.")
    print()
    print("Usage:")
    print("  python build_piece_matrix.py --input <input.json> [--output <output.json>] [--config <config.json>]")
    print("  python build_piece_matrix.py --inputdir <input_dir> [--outputdir <output_dir>] [--recursive] [--pattern <glob>] [--global-thresholds] [--config <config.json>]")
    print("  python build_piece_matrix.py --input <input.json> --list <prop1[,prop2,...]> [--search <Top|Top:Sub>] [--config <config.json>]")
    print("  python build_piece_matrix.py --help")
    print("  python build_piece_matrix.py --man")
    print()
    print("Arguments:")
    print("  --input   Path to the source piece dump JSON (required for normal run)")
    print("  --inputdir Input directory of piece dump JSON files (batch mode)")
    print("  --output  Optional output JSON file path (normal run)")
    print("  --outputdir  Optional output directory for --inputdir mode (default: --inputdir)")
    print("  --pattern Optional glob for --inputdir mode (default: *.json)")
    print("  --recursive  Recursively scan subdirectories in --inputdir mode")
    print("  --global-thresholds  In --inputdir mode, compute top/sub caps from all files together")
    print(f"  --config  Optional config JSON path (default: {DEFAULT_CONFIG_FILENAME})")
    print("  --search  Optional slice under exact (e.g., Crafting or Crafting:Iron)")
    print("  --list    Comma-separated item properties to print (all items if --search omitted)")
    print("  --help    Show this brief help")
    print("  --man     Show detailed manual")


def print_man_page() -> None:
    """
    Print detailed manual page.
    """
    print(
        f"""
NAME
    build_piece_matrix.py - classify Valheim build pieces into a matrix with diagnostics

SYNOPSIS
    python build_piece_matrix.py --input <input.json> [--output <output.json>] [--config <config.json>]
    python build_piece_matrix.py --inputdir <input_dir> [--outputdir <output_dir>] [--recursive] [--pattern <glob>] [--global-thresholds] [--config <config.json>]
    python build_piece_matrix.py --help
    python build_piece_matrix.py --man

DESCRIPTION
    This tool reads a Valheim piece dump JSON and produces a combined output JSON containing:
    - exact: hierarchical matrix of top category -> subcategory -> item records
    - summary and diagnostics fields for tuning classifications

    Item records include both source fields and classification reasoning so you can audit placements.

REQUIRED INPUT SHAPE
    Top-level object:
      {{
        "pieces": [ ... ]
      }}

    Piece fields supported by the classifier:
      Prefab / prefab        string
      name                   string
      category               string
      craftingStation        string
      interactionHooks       array[string]
      systemEffects          array[string]
      required               array[{{"required": string, "amount": int}}] (plus tolerant variants)

OPTIONS
    --input <path>
        Input piece dump JSON path.
        Required for normal execution.

    --output <path>
        Optional output JSON file path.
        If omitted, output defaults to:
          <input-stem>-Classification<input-suffix>
        in the same directory as input.

    --inputdir <path>
        Batch mode input directory.
        Processes matching JSON files and writes one output JSON per input.

    --outputdir <path>
        Optional output directory for --inputdir mode.
        Defaults to --inputdir when omitted.

    --pattern <glob>
        Optional filename filter used in --inputdir mode.
        Default: *.json

    --recursive
        In --inputdir mode, recurse through subdirectories.

    --global-thresholds
        In --inputdir mode, run a two-pass classifier:
        - pass 1: classify all files to build shared top/sub cap decisions
        - pass 2: apply those shared caps to each file output
        This enforces consistent max/min threshold behavior across the batch.

    --config <path>
        Optional classifier config JSON.
        If omitted, defaults to:
          {DEFAULT_CONFIG_FILENAME}
        resolved in the same directory as build_piece_matrix.py.

    --help
        Prints a concise usage and argument summary.

    --man
        Prints this detailed manual and exits.

    --search <node>
        Optional query node under "exact" in the generated output model.
        Supported forms:
          TopCategory
          TopCategory:SubCategory
        Examples:
          Crafting
          Crafting:Iron

    --list <prop1[,prop2,...]>
        Print selected properties for each item as a table.
        If --search is omitted, all items under exact are listed.
        Examples:
          name
          prefab,name
          name,prefab,category,craftingStation

CONFIG BASICS
    Config file is JSON with:
      "config_version": "1.0"

    Major configurable sections:
      limits
      materials
      classification
      overrides
      split

    Notes:
    - Rule ordering matters for regex arrays. Keep specific rules before broader patterns.
    - "_comment" keys are ignored by the loader.
    - Rule collections that need comments use object form:
        {{"_comment": "...", "rules": [ ... ]}}

OVERRIDE STRATEGY (HIGH LEVEL)
    1. placement_rules
       If matched, force top and optional subcategory.
    2. subcategory overrides/rules
       regex overrides, material precedence, fallback scoring.
    3. post-processing
       family normalization, top merge cap, subcategory cap into "Other".

OUTPUT OVERVIEW
    Output JSON top-level keys:
      exact
      summary
      observed_materials
      material_frequency_counts
      material_presence_counts
      material_prescence_counts
      possible_alias_collisions

    Each item record in exact includes:
      name, prefab, category, craftingStation, source_category,
      interaction_hooks, system_effects,
      top_category, subcategory_raw, subcategory,
      reasoning, top_reasons, sub_reasons,
      required

EXAMPLES
    Default config:
      python build_piece_matrix.py --input BuildMenuPieceDump.json --output "BuildMenuPieceDump - matrix.json"

    Custom config:
      python build_piece_matrix.py --input BuildMenuPieceDump.json --output out.json --config my_rules.json

    List all items (all exact nodes) with selected columns:
      python build_piece_matrix.py --input BuildMenuPieceDump.json --list name,prefab

    Search one subcategory and list names:
      python build_piece_matrix.py --input BuildMenuPieceDump.json --search Crafting:Iron --list name

EXIT BEHAVIOR
    - Exits with error on invalid JSON, missing files, invalid config version, or invalid regex in config.
    - Exits successfully after writing output and printing a console summary.
    - In search mode (--search + --list), no output file is written; only projected JSON is printed.
"""
    )


def extract_exact_slice(
    exact: dict[str, Any],
    search_path: str,
) -> Any:
    """
    Extract a slice from the "exact" tree using path formats:
      TopCategory
      TopCategory:SubCategory
    """
    parts = [p.strip() for p in str(search_path).split(":", 1)]
    top = parts[0] if parts else ""
    if not top:
        raise ValueError("Search path is empty.")

    if top not in exact:
        raise KeyError(f"Top category not found in exact: {top!r}")

    top_slice = exact[top]
    if len(parts) == 1:
        return top_slice

    sub = parts[1]
    if not sub:
        raise ValueError("Subcategory path segment is empty.")
    if sub not in top_slice:
        raise KeyError(f"Subcategory not found in exact[{top!r}]: {sub!r}")
    return top_slice[sub]


def parse_list_properties(raw: str) -> list[str]:
    """
    Parse comma-separated property names for --list.
    """
    props = [p.strip() for p in str(raw).split(",")]
    props = [p for p in props if p]
    if not props:
        raise ValueError("--list must include at least one property name.")
    return props


def project_slice_properties(slice_value: Any, properties: list[str]) -> Any:
    """
    Keep tree shape, but project each item record to selected properties.
    """
    if isinstance(slice_value, list):
        projected_list: list[Any] = []
        for item in slice_value:
            if not isinstance(item, dict):
                projected_list.append(None)
                continue
            projected_list.append({prop: item.get(prop) for prop in properties})
        return projected_list
    if isinstance(slice_value, dict):
        return {
            key: project_slice_properties(value, properties)
            for key, value in slice_value.items()
        }
    return None


def _stringify_cell(value: Any) -> str:
    if value is None:
        return ""
    if isinstance(value, (dict, list)):
        return json.dumps(value, ensure_ascii=False)
    return str(value)


def build_search_rows(
    exact: dict[str, Any],
    search_path: str | None,
    properties: list[str],
) -> list[dict[str, str]]:
    """
    Build flat tabular rows from an exact-tree search.
    Always includes "node" as Top:Sub per item.
    """
    rows: list[dict[str, str]] = []

    if search_path is None or str(search_path).strip() == "":
        for top, top_slice in exact.items():
            if not isinstance(top_slice, dict):
                continue
            for sub, items in top_slice.items():
                if not isinstance(items, list):
                    continue
                node_label = f"{top}:{sub}"
                for item in items:
                    if not isinstance(item, dict):
                        continue
                    row: dict[str, str] = {"node": node_label}
                    for prop in properties:
                        row[prop] = _stringify_cell(item.get(prop))
                    rows.append(row)
        return rows

    parts = [p.strip() for p in str(search_path).split(":", 1)]
    top = parts[0] if parts else ""
    if not top:
        raise ValueError("Search path is empty.")
    if top not in exact:
        raise KeyError(f"Top category not found in exact: {top!r}")

    if len(parts) == 1:
        top_slice = exact[top]
        if not isinstance(top_slice, dict):
            raise ValueError(f"exact[{top!r}] is not a subcategory map.")
        for sub, items in top_slice.items():
            if not isinstance(items, list):
                continue
            node_label = f"{top}:{sub}"
            for item in items:
                if not isinstance(item, dict):
                    continue
                row: dict[str, str] = {"node": node_label}
                for prop in properties:
                    row[prop] = _stringify_cell(item.get(prop))
                rows.append(row)
        return rows

    sub = parts[1]
    if not sub:
        raise ValueError("Subcategory path segment is empty.")
    top_slice = exact[top]
    if not isinstance(top_slice, dict) or sub not in top_slice:
        raise KeyError(f"Subcategory not found in exact[{top!r}]: {sub!r}")
    items = top_slice[sub]
    if not isinstance(items, list):
        raise ValueError(f"exact[{top!r}][{sub!r}] is not a list of items.")
    node_label = f"{top}:{sub}"
    for item in items:
        if not isinstance(item, dict):
            continue
        row = {"node": node_label}
        for prop in properties:
            row[prop] = _stringify_cell(item.get(prop))
        rows.append(row)
    return rows


def print_search_table(rows: list[dict[str, str]], properties: list[str]) -> None:
    """
    Print search rows as a padded table with header and dashed separator.
    """
    columns = ["node", *properties]
    widths: dict[str, int] = {col: len(col) for col in columns}
    for row in rows:
        for col in columns:
            widths[col] = max(widths[col], len(row.get(col, "")))

    header = " | ".join(col.ljust(widths[col]) for col in columns)
    separator = "-+-".join("-" * widths[col] for col in columns)

    print(header)
    print(separator)
    for row in rows:
        print(" | ".join(row.get(col, "").ljust(widths[col]) for col in columns))


def default_output_name_for_input(input_path: Path) -> str:
    return f"{input_path.stem}-Classification{input_path.suffix or '.json'}"


def resolve_single_output_path(input_path: Path, output_arg: str | None) -> Path:
    default_output_name = default_output_name_for_input(input_path)
    if not output_arg:
        return input_path.with_name(default_output_name)
    output_path = Path(output_arg)
    return output_path


def discover_input_files(input_dir: Path, pattern: str, recursive: bool) -> list[Path]:
    iterator = input_dir.rglob(pattern) if recursive else input_dir.glob(pattern)
    files = sorted([p for p in iterator if p.is_file()])
    return files


def build_combined_output(matrix: SplitMatrix, diagnostics: Diagnostics) -> dict[str, Any]:
    return {
        "exact": matrix.get("exact", {}),
        **diagnostics,
    }


def main() -> None:
    """
    Command-line entry point.

    Usage:
        python build_piece_matrix.py --input input.json --output output_matrix.json

    Returns:
        None

    Side effects:
        - reads input JSON
        - writes combined matrix+diagnostics JSON
        - prints console summary
    """
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--input", required=False, help="Input JSON path")
    parser.add_argument("--inputdir", required=False, dest="input_dir", help="Input directory (batch mode)")
    parser.add_argument("--output", required=False, help="Output JSON path")
    parser.add_argument("--outputdir", required=False, help="Output directory for --inputdir mode")
    parser.add_argument("--pattern", required=False, default="*.json", help="Glob filter for --inputdir mode")
    parser.add_argument("--recursive", action="store_true", help="Recurse subdirectories in --inputdir mode")
    parser.add_argument("--global-thresholds", action="store_true", dest="global_thresholds",
                        help="Use shared top/sub thresholds across all files in --inputdir mode")
    parser.add_argument(
        "--config",
        required=False,
        help=f"Optional config JSON path (default: {DEFAULT_CONFIG_FILENAME})",
    )
    parser.add_argument("--search", required=False, help="Slice under exact: Top or Top:Sub")
    parser.add_argument(
        "--list",
        required=False,
        dest="list_properties",
        help="Comma-separated properties to print per item in selected slice",
    )
    parser.add_argument("--help", action="store_true", dest="show_help", help="Show brief help")
    parser.add_argument("--man", action="store_true", dest="show_man", help="Show detailed manual")
    args = parser.parse_args()

    if args.show_help:
        print_short_help()
        return

    if args.show_man:
        print_man_page()
        return

    search_mode = bool(args.list_properties)

    if bool(args.input) == bool(args.input_dir):
        print("Error: provide exactly one of --input or --inputdir.")
        print()
        print_short_help()
        raise SystemExit(2)

    if args.input_dir and search_mode:
        print("Error: --list/--search are only supported with --input mode.")
        print()
        print_short_help()
        raise SystemExit(2)

    if args.search and not args.list_properties:
        print("Error: --search requires --list.")
        print()
        print_short_help()
        raise SystemExit(2)

    config_path = (
        Path(args.config)
        if args.config
        else Path(__file__).with_name(DEFAULT_CONFIG_FILENAME)
    )

    config = load_config(config_path)
    apply_config(config)

    if args.input:
        input_path = Path(args.input)
        if not input_path.exists() or not input_path.is_file():
            raise ValueError(f"--input must be a file path: {input_path}")
        with input_path.open("r", encoding="utf-8") as f:
            data: RawJsonDocument = json.load(f)

        matrix, diagnostics = build_matrix_and_diagnostics(data)
        combined_output = build_combined_output(matrix, diagnostics)

        if search_mode:
            properties = parse_list_properties(args.list_properties)
            rows = build_search_rows(combined_output.get("exact", {}), args.search, properties)
            print_search_table(rows, properties)
            return

        output_path = resolve_single_output_path(input_path, args.output)
        if output_path.exists() and output_path.is_dir():
            raise ValueError(f"--output must be a file path, not a directory: {output_path}")
        with output_path.open("w", encoding="utf-8") as f:
            json.dump(combined_output, f, indent=2, ensure_ascii=False)

        print_console_summary(matrix, diagnostics)
        print(f"Wrote output: {output_path}")
        return

    input_dir = Path(args.input_dir)
    if not input_dir.exists() or not input_dir.is_dir():
        raise ValueError(f"--inputdir path is not a directory: {input_dir}")
    output_dir = Path(args.outputdir) if args.outputdir else input_dir
    output_dir.mkdir(parents=True, exist_ok=True)

    input_files = discover_input_files(input_dir, args.pattern, args.recursive)
    if not input_files:
        raise ValueError(f"No input files matched in {input_dir} using pattern {args.pattern!r}")

    if not args.global_thresholds:
        for input_path in input_files:
            with input_path.open("r", encoding="utf-8") as f:
                data: RawJsonDocument = json.load(f)
            matrix, diagnostics = build_matrix_and_diagnostics(data)
            combined_output = build_combined_output(matrix, diagnostics)
            output_path = output_dir / default_output_name_for_input(input_path)
            with output_path.open("w", encoding="utf-8") as f:
                json.dump(combined_output, f, indent=2, ensure_ascii=False)
            print(f"Wrote output: {output_path}")
            print_file_warnings(input_path, diagnostics)
        print(f"Processed {len(input_files)} file(s) in independent mode.")
        return

    datasets: list[tuple[Path, list[RawPieceRecord], list[NormalizedPieceRecord]]] = []
    global_material_presence: MaterialCounter = Counter()
    for input_path in input_files:
        with input_path.open("r", encoding="utf-8") as f:
            data = json.load(f)
        raw_pieces = load_pieces(data)
        normalized_records, material_presence = normalize_records_from_raw_pieces(raw_pieces)
        global_material_presence.update(material_presence)
        datasets.append((input_path, raw_pieces, normalized_records))

    prelimit_grouped: list[Matrix] = []
    for _path, _raw_pieces, normalized_records in datasets:
        grouped_exact = classify_records_to_grouped_exact(normalized_records, global_material_presence)
        prelimit_grouped.append(grouped_exact)

    keep_tops, sub_keep = build_global_limit_plan(prelimit_grouped)

    for (input_path, raw_pieces, normalized_records), grouped_exact in zip(datasets, prelimit_grouped):
        grouped_limited, placement_warnings = apply_global_limit_plan(
            grouped_exact,
            keep_tops=keep_tops,
            sub_keep=sub_keep,
        )
        matrix: SplitMatrix = {
            "exact": sort_matrix_for_output(grouped_limited)
        }

        material_frequency_counts: CounterType[MaterialName] = Counter()
        local_presence: MaterialCounter = Counter()
        for record in normalized_records:
            seen_materials = set()
            for material, amount in record["required_items"]:
                material_frequency_counts[material] += amount
                seen_materials.add(material)
            local_presence.update(seen_materials)

        observed_materials = collect_observed_materials(raw_pieces)
        diagnostics: Diagnostics = {
            "summary": summarize_matrix(matrix),
            "observed_materials": observed_materials,
            "material_frequency_counts": dict(sorted(material_frequency_counts.items())),
            "material_presence_counts": dict(sorted(local_presence.items())),
            "material_prescence_counts": dict(sorted(local_presence.items())),
            "possible_alias_collisions": find_possible_alias_collisions(observed_materials),
            "placement_warnings": placement_warnings,
        }
        combined_output = build_combined_output(matrix, diagnostics)
        output_path = output_dir / default_output_name_for_input(input_path)
        with output_path.open("w", encoding="utf-8") as f:
            json.dump(combined_output, f, indent=2, ensure_ascii=False)
        print(f"Wrote output: {output_path}")
        print_file_warnings(input_path, diagnostics)

    print(
        f"Processed {len(input_files)} file(s) with shared thresholds "
        f"(tops={len(keep_tops)})."
    )


if __name__ == "__main__":
    main()
