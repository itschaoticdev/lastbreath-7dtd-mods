# Chaotic Multi-Tool

**For 7 Days to Die V3.1**

It's a shovel. It's also a pickaxe, an axe and a skinning knife.

One steel tool that **mines, chops, digs and butchers** — and beats every dedicated steel tool
at its own speciality. Stop carrying four tools and burning four belt slots.

---

## Best at everything

| Job | Chaotic Multi-Tool | Best vanilla tool |
| --- | --- | --- |
| Wood | **100** | Steel axe 89 |
| Metal | **100** | Steel pickaxe 85 |
| Earth | **110** | Steel shovel 100 |
| Stone | **180** | Steel pickaxe 153 |
| Gravel | **180** | Steel pickaxe 153 |
| Butchering | **5 / yield 1.0** | Hunting knife 5 / 1.0 |

It also inherits the steel axe's entity damage (29, highest of the three) and the steel shovel's
stamina cost (24.7, lowest of the three).

## Mods still work

Full `3,3,3,4,4,4` mod slots across quality tiers, exactly matching the steel tools, and it keeps
the `melee` / `tool` / `axe` / `shovel` / `miningTool` tags that tool mods check against. Every
tool mod you already use will fit.

## Crafting

Workbench, 120s. Costs the parts for a full steel tool set, so it's an upgrade path rather than
a free strict improvement:

- 3x Steel Tool Parts (`meleeToolAllSteelParts` — the shared parts item for the steel pickaxe,
  axe and shovel, so three of them is exactly one full set)
- 25x Forged Steel
- 10x Duct Tape

It asks for parts rather than the finished tools on purpose. Items with `ShowQuality="true"`
cannot be used as recipe ingredients in 7DtD — the crafting UI can't count them, so the
requirement renders with no number and the recipe stays uncraftable no matter what you're
carrying. Vanilla has the same constraint; every tool and weapon recipe consumes a non-quality
`...Parts` item instead.

## Progression

It rides the **Harvesting Tools** crafting skill, on the same levels as the steel tool set:

| Skill level | Unlocks |
| --- | --- |
| 26 | Recipe unlocked, craftable at Q1 |
| 32 / 39 / 46 / 53 / 60 | Q2 / Q3 / Q4 / Q5 / Q6 |

Higher quality costs more, the same way vanilla tools do: the forged steel and duct tape scale
up to 6x at Q6, which also needs **1x Legendary Parts**. The steel tool parts deliberately do
*not* scale — 18 parts for one tool would be absurd.

## Requirements

- 7 Days to Die **V3.1**
- Nothing else. No DLL, no Harmony, EAC-safe.

## Installation

Drop the `ChaoticMultiTool` folder into your `Mods` folder:

- **Dedicated server:** `<server dir>/Mods/ChaoticMultiTool`
- **Singleplayer:** `%APPDATA%/Roaming/7DaysToDie/Mods/ChaoticMultiTool`

Restart the game or server.

**Server owners: only the server needs this.** It is pure XML and marked `ServerSideOnly`, and it
reuses the vanilla steel pickaxe model and icon, so your players install nothing at all.

## Notes

- Uses the vanilla steel pickaxe as its held model and icon by design — that is what keeps it a
  zero-install mod.
- Adds no blocks and overwrites no vanilla items. The steel pickaxe, axe, shovel and hunting
  knife are all untouched and still craftable as normal.
- Balance is deliberately "strictly best" — it is meant to be the endgame gathering tool. If you
  want it toned down, edit the `DamageModifier` and `BlockDamage` values in `Config/items.xml`.
- To gate it behind the mechanical tier (auger/chainsaw) instead of steel, change the unlock
  level to 61 and the quality levels to 68,76,84,92,100 in `Config/progression.xml`.

Built and tested against V3.1.0 (b14).
