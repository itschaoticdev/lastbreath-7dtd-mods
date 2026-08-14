# Zombie Speed Control

**7 Days to Die V3.x — server-side only, no client install, EAC safe.**

Decide exactly how fast zombies are. Per tier, per time of day, per individual zombie
if you want. Walkers by day and sprinters after dark, everything shambling, or
nightmare-speed ferals — it's a config file, not a fixed opinion.

---

## Why not just edit entityclasses.xml

Because there are 63 zombie classes that define a speed, they inherit from each other
in a chain, several of them are **commented out** in the vanilla file, and the V3.1
"Charged" tier defines only a movement *pattern* while inheriting its real speed from
its Radiated parent. A blanket `//MoveSpeedAggro` find-and-replace hits things you
didn't mean to hit — including boss types balanced around their own speeds.

This mod ships a generator that reads your actual install, works out the real class
list, and writes precise per-class patches from a config file you control.

---

## Defaults

| Group | Classes | Day | Night | Vanilla was |
| --- | ---: | --- | --- | --- |
| normal | 23 | walk | jog | 0.2 / 1.25 |
| feral | 28 | jog | run | 0.6 / 1.45 |
| radiated | 4 | run | sprint | ~0.5 / 1.35 |
| special | 8 | *(off)* | *(off)* | untouched |

"special" — spiders, cops, demolishers, screamers, wights — is **off by default**.
Those are balanced around their own movement and slowing a Demolisher or speeding a
Behemoth changes fights a lot. Turn it on if you want full control.

---

## Install

Drop the `zzzzZombieSpeedControl` folder into your server's `Mods` folder and restart.

Server-side only — players install nothing. Don't rename the folder; the `zzzz`
prefix makes it load last so its patches win over other mods that touch zombie stats.

---

## Changing the speeds

Edit `tools/config.json`, then rebuild (needs [Node.js](https://nodejs.org)):

```
cd tools
node build.js
```

Restart the server to load the result.

### Named speeds

Use a preset name or a raw number anywhere a speed is expected:

```
still 0.0 · crawl 0.06 · shamble 0.12 · walk 0.25 · brisk 0.4
jog 0.7 · run 1.1 · sprint 1.45 · nightmare 1.9
```

### A group

```json
"normal": {
  "enabled": true,
  "dayChase": "walk",      // MoveSpeedAggro, first value
  "nightChase": "jog",     // MoveSpeedAggro, second value
  "wander": 0.08,          // MoveSpeed - idle shuffling
  "randomness": [0, 0.05], // MoveSpeedRand, added on top of the DAY figure
  "panic": null            // MoveSpeedPanic; null = leave vanilla alone
}
```

`enabled: false` leaves that whole group vanilla.

**Watch `randomness`** — it's added to the day speed, so a wide band quietly turns a
"walk" setting into a jog. That is the single most common reason a slow-zombie setup
doesn't feel slow.

### One specific zombie

In `perZombie`, replace `null` with an override:

```json
"zombieSpider": { "dayChase": "run", "nightChase": "sprint" },
"zombieFatCop": { "skip": true }
```

`skip: true` leaves that one completely vanilla.

### After installing a zombie mod

```
node build.js scan
node build.js
```

`scan` re-reads your install, adds any new zombie classes, prunes ones that vanished,
and keeps every override you set.

---

## What it actually writes

Precise, per-class patches — never a blanket selector:

```xml
<set xpath="/entity_classes/entity_class[@name='zombieArlene']/property[@name='MoveSpeedAggro']/@value">0.25, 0.7</set>
```

It only patches classes that genuinely **define** the property. Classes that inherit
are covered by patching their parent, and a `<set>` matching nothing would just log
an xpath failure.

---

## Notes

- Blood moon hordes use these same chase speeds. Slowing normals slows blood moon.
- Zombie *damage* and health are untouched — this is movement only.
- Safe to add or remove mid-playthrough.

Built and tested on **V 3.1.0 (b14)** alongside 40 other mods.
