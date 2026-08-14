# Chaotic Multi-Tool: Legendary Edition

**For 7 Days to Die V3.1**

> **Install this OR the standard Chaotic Multi-Tool — never both.** They define the same item
> and the same recipe, so running both will throw a duplicate-item error on load. The item name
> is identical in both editions on purpose: you can swap a server from one to the other without
> players losing tools they already built.

Same tool. Much harder to get.

It's a shovel. It's also a pickaxe, an axe and a skinning knife — one steel tool that
**mines, chops, digs and butchers**, and beats every dedicated steel tool at its own speciality.

---

## What's different from the standard edition

| | Standard | Legendary |
| --- | --- | --- |
| Unlock | Harvesting Tools level 26 | **Schematic only.** No perk level ever unlocks it |
| Schematic source | — | T5 hardened chests, T5 infested chests, treasure digs |
| Cost | 3x Steel Tool Parts | 3x Steel Tool Parts **+ 1x Chainsaw Parts** |
| Craft time | 120s | 180s |
| Stats | identical | identical |

Nothing about how the tool performs changed. This edition only changes how you earn it.

## The schematic

**Chaotic Multi-Tool Schematic** is the one and only way to learn the recipe. It is not sold by
traders, is not in the general schematic loot pool, and does not appear in T4 or lower containers.

| Source | Chance per container |
| --- | --- |
| T5 hardened chest (tier-5 POI loot room) | **20%** |
| T5 infested chest | **20%** |
| Buried treasure (treasure map dig) | **5%** |

Buried treasure is there so Treasure Hunter players have a slow alternate route rather than a
hard dead end. Every roll is independent, so a T5 chest is a genuine ~1-in-5 rather than the
tool competing for a single bonus slot.

Reading it is permanent and grants 500 XP.

## Crafting

Workbench, 180s:

- **3x Steel Tool Parts** (`meleeToolAllSteelParts` — the shared parts item for the steel
  pickaxe, axe and shovel, so three of them is exactly one full set)
- **1x Chainsaw Parts** (`meleeToolAxeT3ChainsawParts` — the auger/chainsaw tier core)
- 25x Forged Steel
- 10x Duct Tape
- 1x Legendary Parts *(Q6 only)*

The chainsaw part is the point. By the time you can build this you have thousands of forged
steel, but you own exactly one chainsaw core — so the craft costs you something you actually
care about.

### Why it doesn't ask for the finished tools

Requiring you to hand over an actual steel pickaxe, axe, shovel and hunting knife would be the
better story, and it is not possible in XML. Ingredient counting does an exact `ItemValue` match,
so any item with `ShowQuality="true"` — which is every finished tool — can never satisfy a recipe.
The requirement renders with no number and the recipe stays uncraftable forever, even while you
are holding all four. Vanilla hits the same wall; every tool and weapon recipe consumes a
non-quality `...Parts` item instead. Asking for the parts is the closest legal equivalent, and
mechanically it is the same act — you scrap your tools to build it.

## Progression

Harvesting Tools no longer unlocks the tool, but it still decides how good yours comes out:

| Skill level | Quality |
| --- | --- |
| any (with schematic) | Q1 |
| 32 / 39 / 46 / 53 / 60 | Q2 / Q3 / Q4 / Q5 / Q6 |

Higher quality costs more forged steel and duct tape, up to 6x at Q6, which also needs
1x Legendary Parts. The tool parts and chainsaw part deliberately do *not* scale — 18 steel
parts and 6 chainsaw cores for one tool would not be hard, it would be impossible.

## Requirements

- 7 Days to Die **V3.1**
- Nothing else. No DLL, no Harmony, EAC-safe.

## Installation

Drop the `ChaoticMultiToolLegendary` folder into your `Mods` folder:

- **Dedicated server:** `<server dir>/Mods/ChaoticMultiToolLegendary`
- **Singleplayer:** `%APPDATA%/Roaming/7DaysToDie/Mods/ChaoticMultiToolLegendary`

Restart the game or server. Remove `ChaoticMultiTool` first if you have it.

**Server owners: only the server needs this.** It is pure XML and marked `ServerSideOnly`, and it
reuses vanilla models and icons, so your players install nothing at all.

## Tuning

- **Easier to find:** in `Config/loot.xml`, raise `loot_prob_template="low"` (20%) to `"medLow"`
  (35%) or `"med"` (50%), or add `reinforcedChestT3` / `hardenedChestT4` blocks at `"veryLow"`.
- **Harder to find:** delete the `groupBuriedTreasure` block so T5 chests are the only source.
- **Let traders sell it:** add a `traders.xml` appending `ChaoticMultiToolSchematic` to a secret
  stash group. Left out on purpose — a purchasable schematic undoes the whole point.
- **Put the perk unlock back:** add
  `<passive_effect name="RecipeTagUnlocked" operation="base_set" level="26,100" value="1" tags="ChaoticMultiTool"/>`
  to the `append` block in `Config/progression.xml` — but then the schematic is decorative.

## Notes

- Uses the vanilla steel pickaxe as its held model and icon by design — that is what keeps it a
  zero-install mod. The schematic reuses that icon with the tool's gold tint.
- Adds no blocks and overwrites no vanilla items. The steel pickaxe, axe, shovel and hunting
  knife are all untouched and still craftable as normal.
- Full stat breakdown is in the standard edition's README — the numbers are identical.

Built and tested against V3.1.0 (b14).
