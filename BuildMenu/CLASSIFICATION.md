# BuildMenu

A Valheim mod that adds a categorized filtering system to the build menu, making it easier to navigate large sets of build pieces from both vanilla and modded content.

Do you need to read this?  Maybe.  There are already classification files built for some popular MODs that add new items, but certainly not all of them.  However, the default sorting algorithyms probably will work for the vast majority of item adding MODs out there.  If you don't care to get too fancy, but you have access to python, then you can head straight to the INSTALL.md file and not worry about this.

## Classification System

BuildMenu uses JSON files to define how pieces are grouped.

All game ujsed `.json` files are in:

```text
BepInEx/config/BuildMenuSorter/
```

and are loaded at startup.

These files map build-piece prefabs into:

* Primary categories
* Secondary categories

This allows full customization of the build menu structure without modifying the plugin.

## Debugging & Data Extraction

The mod includes tools for working with build-piece data:

* Press `F12` (default) to dump the full build-piece library
* Unknown or unmapped pieces can be logged for troubleshooting
* Verbose logs are written to:

```text
config/BuildMenuSorter/BuildMenuSorter.log
```

## Data
* `GameItemJsons/` Contains source JSON files for vanilla and modded build pieces.  You can see what MODs I run as I have already categorized them as I see fit.  If you don't like it, then you can do it differently using the steps below.  Is it perfect?  Nope.
* `BuildPieceMatrixConfig.json` Configuration for classification generation.  This is where all the magic (well, rules) happens.  View this file to see how the classification system works as it's fairly complicated to explain but much easier to view.  You may get some info below, but it's probably best to read that file (and it has \_comments as well!)

### Helper scripts

These scripts support generating and validating classification data:

* `build_piece_matrix.py` Generates classification outputs (what goes in the `config/BuildMenuSorter` directory) from source data.  This is where everything happens (I guess you can say this is also "where all the magic happens").
* `build_json_compare.py` A utility to take two JSON files and compare all the items in both, it will output a json file with the differences.  This is used for creating mod-specific JSON files.
* `search_build_menu_classification.py` Indepth search for items, categories, and overrides.  Very useful for debugging.
* `print_override_path.py` Displays rule resolution paths.  Essentially this shows currently how an item goes through the classification system.

All the helper scripts have a `--help` flag to get usage information, hence I am not going to repeat that all here.

The `build_piece_matrix.py` script has a `--man` flag to get even more detailed usage information.

## Workflow for Updating Classifications

The following typical workflow may be one way to do all this.  I will try and give some detailed parts, but documentation is not fun and I will miss a bunch of things that I just "know" because I've been working on this.  So forgive me.
* note, all files referenced below may be named differently, but the general idea is the same

### Json Files
Look at the json files included with the MOD if you want.  You will be confused.  But soon all(some) will be revealed.

### Dump build-piece data from the game.  
This is achieved by pressing `F12` (default) in-game.  This will (always) write out a file called `BuildMenuPieceDump.json` in the `config` directory.  This is a JSON file that contains all the build-piece data that the game knows of as per all the MODs you have loaded.
The vanilla game has already been exported @ `GameItemJsons/BuildMenuPieceDump-Vanilla.json`.  The vanilla JSON file has the games original build pieces.  If you have no MODs that have added build pieces to the game, then this will (or should) be exactly the same as the Vanilla file.  But let's pretend it's not.

### Compare the exported JSON  to the vanilla game.
```bash
paloma@dascomputer:/mnt/c/Users/paloma/RiderProjects/ImproperMods/BuildMenu$ python build_json_compare.py --input GameItemJsons/BuildMenuPieceDump-Vanilla.json BuildMenuPieceDump.j
son --output Diff.json
Input file1 (GameItemJsons/BuildMenuPieceDump-Vanilla.json) items: 313
Input file2 (BuildMenuPieceDump.json) items: 322
Exact duplicates found: 313
Changed items found: 0
File1-only items found: 0
File2-only items found: 9
Unique items exported: 9
Wrote: Diff.json
```
What does this tell us?  Well.
* file1 has 313 items
* file2 has 322 items
* there are 313 duplicate items between file1 and file2
* file1 has 0 unique items
* file2 has 9 unique items
* the file Diff.json will now have those 9 unique items.
Exciting!

### An example item
Here's where we need to take a look at the resulting JSON file, as it will be key in determing how to classify items.  Here is a single item (json'ized):

```json
    {
      "Prefab": "cl_piece_groundtorch_black",
      "category": 0,
      "craftingStation": "Forge",
      "interactionHooks": [
        "Fireplace"
      ],
      "name": "Black Iron Torch",
      "pieceTable": "Hammer, _HammerPieceTable",
      "required": [
        {
          "amount": 2,
          "required": "Iron"
        },
        {
          "amount": 1,
          "required": "Coal"
        }
      ],
      "systemEffects": [
        "HeatSource"
      ],
      "token": "Black Iron Torch"
    },
```
If you are doing this, I'm going to assume you know what most of that means.  It's the property breakdown of an item of a custom piece, you won't find this in the vanilla game.  Without any rules, this item would be placed in an "Unknown" top and sub category.  Thankfully, we have lots of rules already.

### List out the current rules.
```bash
python print_override_path.py --input BuildPieceMatrixConfig.json
```
This will list out the current rules.  The output takes the form of:
```text
rule#|Top:Subcategory|override-setting|property|match_type|pattern
```
There are a bunch of default rules, but you can change any of them if you want.  Or leave them be.  
I'll try and go over each one but forgive me if everthing is documented (view the actaul BuildPieceMatrixConfig file to see all the rules):
#### rule#
Just tells you where this rule falls in precedence order.  Should always be 1..<maxRules>
#### Top:Subcategory
This is where you want the item that gets filtered by this rule to be put.  Configured by using "top" and "subcategory" respectively.
#### override-settings
You can ignore this.
#### property
Which property to look at when comparing to the patterns.  These correspond directly to the properties above, albeit in slightly different strings (which is stupid and probably should be fixed, and it may be and I just haven't updated this).  Here they are:
* prefab -> prefab name
* name -> actual name
* combined(prefab+name) -> this essentially combines "prefab"+"name" to match against
* category -> system assigned category
* crafting_station -> craftingStation
* interaction_hooks -> interactionHooks
* system_effects -> systemEffects
#### match_type
You can define rules to be either an exact match (string=string) or a regex (string=\*string\* - or most regular match tokens work, but I have not tested this deeply).  One important caveat - these matches are not case sensitive.  All strings on both sides get lowercased before the match takes effect.  Why?  I dunno (I do, I just don't want to change it).
#### pattern
An array of strings that will match the chosen #property via the chosen #match_type.

#### Example
Here's an example that would filter the above `Black Iron Torch` to the categories of "Crafting:Fire!Fire!" (which is indeed a rule in the shipped classification).
```json
        {
          "property": "interaction_hooks",
          "match_type": "exact",
          "patterns": [
            "Fireplace"
          ],
          "subcategory": "Fire!Fire!",
          "top": "Crafting"
        },
```
How does it do this?  It looks at the property `interaction_hooks` (which again, is interactionHooks in the source file) for an exact_match that has the pattern `Fireplace`.  If you look above, you will see that the item does in fact have an interactionHook with the exact string "Fireplace".  Hence it would get put into `top`:Crafting and `subcategory`:Fire!Fire!

We could have easily changed the "property" to "system_effects" and used the pattern of "HeatSource" to do the same thing.  Or I could use "heatsource" since it's case insensitive.

Now, this hit as rule #6.  If any of the previous rules (1-5) was encountered first for this item, it would have been placed differently.  And yes, this can get tricky.

Again, check out the BuildPieceMatrixConfig.json file for a ton of rules and how to lay them out.  Or just look at the output of the print_override_path.py script.  

### Building the Matrix
You build out the rules and run the following script to actually create the end configuration file that game will load.
```bash
python build_piece_matrix.py --input Diff.json --config BuildPieceMatrixConfig.json --output <pathToValheim>\BepInEx\config\BuildMenuSorter
```
Again, check -h for more help, or --man for lots more help.  But the biggest thing we are doing here is reading in the items, reading in the way we want to filter the items, running the filter matrix across them, and wirting them out to (in this case) the path where the game MOD will read them in.  In this case, a file will be created at `<pathToValheim>\BepInEx\config\BuildMenuSorter\Diff-Classification.json` (you can specifically specify the `--output` name as well, but if you don't it will take the `--input` filename and append `-Classification` to it).

You will also get some output:
```text
Matrix summary
============================================================
exact (1)
  Crafting (1)
    - Fire!Fire!: 1

contains (0)
```
That shows you were the items in your input file are being filtered to.  In this case, 1 item is being filtered to the top category of `Crafting` and subcategory `Fire!Fire!`.  When you do it with files with many more items, you will get different values and numbers.

Let's quickly take a look at the important part of what will be output:
```json
  "exact": {
    "Crafting": {
      "Fire!Fire!": [
        {
          "name": "Black Iron Torch",
          "prefab": "cl_piece_groundtorch_black",
          "category": "0",
          "craftingStation": "Forge",
          "source_category": "0",
          "interaction_hooks": [
            "Fireplace"
          ],
          "system_effects": [
            "HeatSource"
          ],
          "top_category": "Crafting",
          "subcategory_raw": "Fire!Fire!",
          "subcategory": "Fire!Fire!",
          "split": "exact",
          "reasoning": [
            "PLACEMENT_RULES: chosen (placement rule interaction_hooks exact 'Fireplace' -> Crafting:Fire!Fire!; top forced)",
            "PLACEMENT_RULES: chosen (placement rule interaction_hooks exact 'Fireplace' -> Crafting:Fire!Fire!; subcategory forced)",
            "MATERIAL_FAMILY_NORMALIZATION: none"
          ],
          "top_reasons": [
            "placement rule interaction_hooks exact 'Fireplace' -> Crafting:Fire!Fire!"
          ],
          "sub_reasons": [
            "placement rule interaction_hooks exact 'Fireplace' -> Crafting:Fire!Fire!"
          ],
          "split_reasons": [
            "contains output is currently disabled; all records routed to exact"
          ],
          "full_placement_target_top": "Crafting",
          "full_placement_target_subcategory": "Fire!Fire!",
          "required": [
            {
              "required": "Iron",
              "amount": 2
            },
            {
              "required": "Coal",
              "amount": 1
            }
          ]
        }
      ]
    }
  },
```
That's quite a bit, but most of it's self-explanatory.

All items get placed under the `exact` parent node (there are a number of other nodes that have to do with debugging and which I won't cover here, but those are also fairly self-explanatory).

The next node will be a top-level category ("Crafting").  There will be one of these for every top level an item belongs to.
The next node will be the sub-categroy ("Fire!Fire!").  There wil be on of these for every sub-category under a top level that items belongs to.
And then will be a list of items that have been filtered to those combinations.  And each item record contains the same information as in the original file.  But also much more.  All the extra information is again for debugging purpose:
* _top\_category_: It relists the top category.
* _subcategory\_raw_: This can be ignored.
* _subcategory_: It relists the sub category.
* _split_: This can be ignored.
* _reasoning_: An array of it going through rules and why it chose the one it did.  This is really helpful to learn why an item is placed in a certain Top:Subcategory.
* _top\_reasons_: Reason the top category was chosen.
* _sub\_reasons_: Reason the subcategory was chosen.
* _split\_reasons_: This can be ignored.
* _full\_placement\_target\_top_: This can be ignored, but will dupe the top category
* _full\_placement\_target\_subcategory_: This can be ignored, but will dupe the subcategory.

In essence, the main thing to pay attention to is the order in which an item was filtered, and the top and sub reasons why it was placed where it was.

There parameters for this (remember: `-h` or `--man`) utility that can show you more debugging information, how a filter run would pan out, and a host of other things.  This is all used for debugging purposes and if you are creating a Classification, you will likely learn to use those to help you out.

Once the output file have been created, they can put in the `<pathToValheim>\BepInEx\config\BuildMenuSorter` folder (if not already there)  You can run the game and they will be read in and the MOD will sort the build HUD appropriately.

## Searching the Classification Menu
```bash
python search_build_menu_classification.py --tree --files Update.json
```
At it's simplest, this will print out the current tree.  You can use various parameters (ahem: `-h`) to restrict the search, to print out more information on the search, etc.  Again, this is purely for debugging.
