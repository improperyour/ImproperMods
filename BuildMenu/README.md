# BuildMenu

A Valheim mod that adds a categorized filtering system to the build menu, making it easier to navigate large sets of build pieces from both vanilla and modded content.

---

## Overview

Valheim’s default build menu can become difficult to manage when many build pieces are available.  
BuildMenu introduces a structured, JSON-driven categorization system that allows you to:

- Organize pieces into logical groups
- Navigate categories quickly using keyboard shortcuts
- Reduce clutter when using large mod packs
- Customize the menu layout without modifying code

The mod separates build pieces into:

- **Primary categories** (vertical navigation)
- **Secondary categories** (horizontal navigation)
- Optional **search and paging** for large sets

---

## Features

- Category-based filtering for build pieces
- Works with **vanilla and modded content**
- Fully configurable via JSON (no recompilation needed)
- Keyboard-driven navigation
- Paging support for large result sets
- Debug tools for dumping and analyzing build-piece data
- Logging for missing or unknown classifications

---

## Requirements

- Valheim
- BepInEx
- Jotunn (JotunnLib)
- Harmony (0Harmony)

---

## Installation

### Manual install

1. Install BepInEx for Valheim
2. Install Jotunn
3. Copy the compiled plugin:

```text
BuildMenu.dll
```
into:
```text
BepInEx/plugins/
```

4. Create the classification directory (if it does not exist):

```text
BepInEx/config/BuildMenuSorter/
```

5. Place your classification JSON files in that directory (or copy the preconfigured ones with this mod)
6. Launch the game

## Configuration

Configuration of the actual mod is managed via the BepInEx config file generated on first run.

### General
* Enabled — enable/disable the mod
* DefaultPrimaryCategory (ALL) - what is the first Primary Category shown?
* DefaultSecondaryCategory (ALL) - what is the first Secondary Category shown?

### Controls

#### Default navigation keys:

|Action|Default|
|------|-------|
|Primary up/down|W / S|
|Secondary left/right|A / D|
|Previous page|Q|
|Next page|E|

### UI
* UiOffsetLeft (24) - How far over from the left edge of the screen should the UI be?
* UiOffsetTop (124) - How far over from the top edge of the screen should the UI be?

### Debug
* LogUnknownPieces (true) - Log unknown pieces to the mod specific Logfile
* VerboseLogging (false) - Enable verbose logging (this will have lots of output!)
* DumpLibraryShortcut (default: F12) - Dump the full build-piece library to an output file

### Performance
* PerformanceLogging (false) - Enable performance logging
* PerformanceLoggingIntervalSeconds (5) - How often should performance logs be written?
* PerformanceWarningThresholdMs (2) - Tag sections as WARN when average time exceeds this many milliseconds. 

## Notes
* The mod is designed to scale with large modded build-piece sets
* Classification is externalized for flexibility and maintainability
* Unknown or conflicting mappings are handled and can be logged

## .md Files
* This file is README.md, a brief overview of the mod and how to install and use.
* If you want to compile this yourself, read DEVELOPER.md
* If you want to change (or just see how) the Classification system works, read CLASSIFICATION.md
* If you want to know my take on AI and why this mod is about 80% AI and 20% me, read AI.md
* If you want to tell me to go to hell, just `cat HELL.md > dontreallycare@gmail.com`