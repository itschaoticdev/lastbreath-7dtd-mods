# Chaotic's Gore Bait (Lite) — EAC-safe

Thrown gore that pulls a crowd of zombies off your position and holds them there while they eat.

**Pure XML. No DLL. Runs with EAC ON. Players install nothing.**

---

## Why this exists

The full mod, **Chaotic's Zombie Companion**, is a Harmony code mod. Harmony patches assemblies, and
EAC blocks that — so any code mod, from anyone, needs `EACEnabled=false` on the server. That is not
a choice the mod makes; it is what anti-cheat does.

This is the half of that mod which needs no code. If your server keeps EAC on, this is the version
you can run.

## What it does

Throw a pile. Every zombie in range that has not already locked onto you walks over and eats,
and stays put while it does — 10 seconds a zombie for the plain pile, 15 for the ripe one.

| | Radius | Holds for | Eating time |
|---|---|---|---|
| **Gore Bait** | 35m | 45s | 10s per zombie |
| **Ripe Gore Bait** | 55m | 90s | 15s per zombie |

Use it to open a route, empty a street, or pull a wave off a wall before a horde night.

## What it cannot do, and why

**It cannot break a chase that is already happening.** `EAIApproachDistraction.CanExecute` returns
false while the zombie holds an attack target. The full mod fixes that with a Harmony prefix that
clears the target and holds a short window where it may not re-acquire; there is no XML equivalent.

The bait is not wasted, though — an eat-distraction stays *pending* on that zombie rather than being
discarded, so the moment it loses you, it goes for the pile. Break line of sight and the bait does
the rest. Treat it as a pre-emptive tool, not a panic button.

**It cannot turn a zombie into a companion.** No thralls, no roster, no `/thrall` commands. Counting
meals per zombie per player, rewriting a zombie's AI so it follows and fights for you, keeping it
out of the save file, holding horde aggro onto it — all of that is code.

**One pile is not one zombie.** Vanilla pulses a decoy at every eligible zombie in radius; the
one-per-pile cap lives in the DLL. That is a downgrade for taming and an upgrade for crowd control,
which is why these numbers are tuned for area denial rather than the full mod's tighter, shorter pull.

## Install

Drop the `ChaoticGoreBait_Lite` folder into your server's `Mods/` folder and restart. Works with EAC
on or off. Players download nothing.

**Install this OR `ChaoticZombieCompanion`, never both** — they define the same two items, and the
game will not load two items with the same name.

Recipes are identical in both, so a server that later turns EAC off can swap Lite for the full mod
without re-teaching its players anything.

## Crafting

| Item | Craft |
|---|---|
| **Gore Bait** | 4 rotting flesh + 2 bone, no station, makes 2 |
| **Ripe Gore Bait** | 2 gore bait + 8 rotting flesh + 1 acid, campfire + cooking pot |

Rotting flesh comes off zombie corpses, so the loop feeds itself. No magazines, no perks, no trader.

---

Made by Chaotic. Full version: **Chaotic's Zombie Companion** (requires EAC off).
