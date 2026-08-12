# LastBreath 7 Days to Die Mods

Server-side mods written for the [LastBreath](https://discord.gg/5P3cQ2uJFa) 7 Days to Die
servers, running V3.0.1. Most are pure XML modlets that need no client install; the three
Harmony mods ship their C# source alongside the DLL.

Hosted by [Breakneck Hosting](https://breakneckhosting.com).

## The mods

| Mod | What it does | Client install? |
|---|---|---|
| `ChaoticMultiTool` | One tool that acts as axe, pickaxe, shovel and wrench, so you stop carrying four. | No |
| `ChaoticBeastmaster` | Tame animals by throwing meat at them; they follow and fight for you. Harmony/C#. | No |
| `ChickenScoop` | Re-purposes the plow as a scoop for picking up small animals. Harmony/C#. | No |
| `ChaoticJarsAndCansScrap` | Empty jars and cans scrap into usable materials instead of being dead weight. | No |
| `zzzLB_EasySurvival` | Gentler survival curve — food, water and stamina tuned for a working adult's play session. | No |
| `zzzLB_ForgeSlots` | Four simultaneous smelt slots in the forge instead of three. | No |
| `zzzLB_Welcome` | Welcome message and a starter kit on first join. | No |
| `zzzzLB_CraftEverything` | Unlocks crafting recipes that are otherwise loot- or trader-gated. | No |
| `zzzzZombieSpeedControl` | Per-time-of-day zombie speed control, so you can have walkers by day and runners at night. | No |
| `BSM_DataApi` | Companion mod for [Breakneck Server Manager](https://github.com/itschaoticdev/breakneck-server-manager) — exposes player positions, inventories and save-file stats over the Web API. Harmony/C#. | No |
| `zzz_BSM_Tuning` | Applies gameplay tuning sliders set in the Breakneck panel as an XPath modlet. | No |

## Installing

Copy the folder you want into your server's `Mods/` directory and restart:

```
7 Days To Die/Mods/<ModName>/
```

All of these are **server-side**. Players do not need to install anything.

The `zzz` / `zzzz` prefixes are load order, not decoration — 7DtD applies modlets
alphabetically, and these have to patch after the mods they adjust. Keep the names.

## Building the C# mods

`ChaoticBeastmaster`, `ChickenScoop` and `BSM_DataApi` include a `src/` folder. They target
.NET Standard 2.1 and reference `0_TFP_Harmony`. Build against your own game install's
`7DaysToDie_Data/Managed/` assemblies and drop the resulting DLL next to `ModInfo.xml`.

## A note on versions

Written for **V3.0.1**. Mods built for A21 or V1 generally do not work on V3, and the reverse
is also true — if something misbehaves, check the version before anything else.

## Licence

GPLv3 — see [LICENSE](LICENSE). Use them, change them, run them on your server. If you
distribute a modified version, publish your changes too.

In-game 7 Days to Die mods must remain free to end users under The Fun Pimps' policy, and
nothing here is to be sold.
