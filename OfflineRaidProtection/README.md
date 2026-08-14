# Offline Raid Protection

**7 Days to Die V3.x — server-side only, no client install.**

Claimed bases are protected while their owner is offline. With a grace period so
nobody escapes a raid by pulling the plug, optional raid hours, and clan awareness
so a group can't park one member offline as a permanent shield.

---

## The problem

Vanilla gives you exactly one knob — `LandClaimOfflineDurabilityModifier` — which
multiplies block durability while the owner is away. There is no grace period, no
schedule, and it has no idea that the owner's whole clan might be online right now
defending the place.

So PvP servers end up choosing between "bases get flattened while people sleep" and
"turn the multiplier so high nothing is ever raidable."

## What this does

- **True protection while offline.** Either effectively indestructible, or a
  durability multiplier you pick.
- **Grace period.** Protection doesn't kick in until the owner has been gone a set
  number of minutes, so alt-F4 mid-raid doesn't save anyone.
- **Raid windows.** Optionally allow raiding only during set hours of real server
  time. Outside the window offline bases are protected; inside it they're fair game.
- **Clan aware.** If any member of the owner's saved party is online, the base counts
  as defended and is raidable as normal. (Needs the companion mod — see below.)

It works by riding vanilla's own claim system rather than fighting it: the game
already funnels all claimed-block durability through a single multiplier, and this
mod adjusts that one value. No damage maths is reimplemented, so explosives, vehicles
and every weapon type behave consistently for free.

---

## Install

Drop the `OfflineRaidProtection` folder into your server's `Mods` folder and restart:

```
7DaysToDieServer/Mods/OfflineRaidProtection/
```

**Requires EAC to be off** (`EACEnabled=false` in serverconfig.xml) — true of every
C# mod for this game. Players install nothing.

---

## Configuration

`OfflineRaidProtection.cfg`, written on first start. Restart to apply.

| Setting | Default | What it does |
| --- | --- | --- |
| `enabled` | `true` | Master switch. |
| `mode` | `immune` | `immune` = effectively indestructible. `multiplier` = use the value below. |
| `protection_multiplier` | `32` | Durability multiplier when `mode=multiplier`. Vanilla's own offline default is 32 for comparison. |
| `grace_minutes` | `10` | Minutes offline before protection engages. |
| `party_aware` | `true` | Treat the base as defended if a saved party-mate is online. |
| `raid_window` | *(empty)* | e.g. `18:00-23:00`. Real server clock, 24h. Empty = protected around the clock. Windows crossing midnight (`22:00-02:00`) work. |
| `debug_log` | `false` | One line per protection decision. Noisy — testing only. |

The mod never makes a base *weaker* than vanilla would have; it only ever raises
protection.

---

## Clan awareness

`party_aware=true` needs the **Persistent Parties** mod installed alongside this one.
It reads that mod's saved party file — there's no hard dependency between the two
assemblies, so either mod works perfectly well on its own. Without Persistent
Parties, `party_aware` simply does nothing and protection is based purely on whether
the claim owner personally is online.

This matters on clan servers: without it, a five-person group leaves one account
logged out and their base is untouchable forever.

---

## Suggested setups

**Casual PvP** — nobody loses a base overnight:
```
mode=immune
grace_minutes=10
```

**Serious PvP with scheduled raid nights** — bases only crackable during prime time:
```
mode=immune
grace_minutes=5
raid_window=19:00-23:00
```

**Soft protection** — offline bases are tougher but still crackable with effort:
```
mode=multiplier
protection_multiplier=64
grace_minutes=15
```

---

## Compatibility

Built and tested on **V 3.1.0 (b14)** alongside 40 other mods. A single narrow
Harmony patch on one vanilla method, so conflicts are unlikely — though any other mod
that also rewrites claim durability will fight with this one. Safe to add or remove
mid-playthrough; nothing in your save is modified.
