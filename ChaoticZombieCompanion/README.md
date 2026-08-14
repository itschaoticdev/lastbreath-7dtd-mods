# Chaotic's Zombie Companion

Turn the horde against itself. Throw gore bait, break a chase, feed the same zombie until it stops
being your problem and starts being your muscle.

**7 Days to Die V3.x - server-side, no client install.**

---

## What it does

**The bait is an escape tool first.** Throw a pile of gore and the zombie chasing you breaks off and
stops to eat. One zombie per pile - a horde will not all stop for the same meal - so it buys you an
opening, not a free pass.

**Feed the same one enough times and it takes the leash.** Four meals for a plain walker, more for
the harder tiers. It then:

- follows you, and catches up if you drive off
- hunts every other zombie, bandit and enemy animal in sight
- **never** touches a player, including in PvP, including the owner who just shot it by accident
- **does not break blocks** - the block-breaking AI is stripped, so it can follow you home
- comes back after a relog, a restart, or a chunk unload

**The horde comes for it.** Half the zombies within 14m of your thrall switch targets to it, and
anything it hits fights back. That is the point of the mod - a thrall is something you fight
*behind*. The share is deliberately partial (`ThrallTauntShare=0.5`): the other half is still your
problem, so a blood moon does not become a spectator sport.

Vanilla will not do any of this on its own. See "Why zombies cannot fight zombies" below.

You hold six and have two out at once by default. Both numbers are config.

### Feed costs

| Tier | Meals |
|------|-------|
| Walker | 4 |
| Feral | 6 |
| Radiated | 8 |
| Charged / Infernal | 10 |

Ripe Gore Bait counts as two meals, so a radiated zombie is four ripe piles rather than eight
ordinary ones - which matters, because you have to stand near it the whole time.

### What cannot be turned

- **Demolishers** - they detonate, and they would follow you home first.
- **Screamers** - the scream calls a horde onto its own position, i.e. onto you.
- **Zombie dogs, bears, boars and vultures** - they eat the gore and break off a chase, but they
  belong to *Chaotic's Beastmaster*. Both mods can run together; they do not fight over anything.

Both exclusions are config (`ExcludeZombies=`). Clear the line if you want the carnage.

---

## Items

| Item | Craft | Effect |
|------|-------|--------|
| **Chaotic's Gore Bait** | 4 rotting flesh + 2 bone, no station, makes 2 | 30m pull, 20s window, 1 meal |
| **Chaotic's Ripe Gore Bait** | 2 gore bait + 8 rotting flesh + 1 acid, campfire + cooking pot | 45m pull, 30s window, eaten faster, **2 meals** |

Rotting flesh comes off zombie corpses, so the loop feeds itself: the only way to get more thralls
is to kill more zombies. No magazines, no perks, no trader gate.

Fed to a zombie that is already yours, gore heals it instead (25% / 50% of max) and resets its
decay timer if the server runs one.

---

## Commands

Chat, so they work with no client install and need no admin rights.
`/thrall`, `/thralls`, `/zombie`, `/zombies`, `/zc` and `/z` are all the same command.

```
/thrall list          what you are holding
/thrall call <n>      bring one out
/thrall stow [n]      put one away, or all of them
/thrall release <n>   let one go for good
```

Admin console (`zc`):

```
zc check              re-run the startup self-check
zc scan [radius]      live zombies: turnable, AI task, target, distraction
zc items              gore bait lying in the world and its distraction state
zc thralls            every live thrall and who owns it
zc list               every entity class this mod will turn
zc thrall <verb> [n] [player]   run a /thrall command for someone
zc trace on|off       verbose logging, no restart needed
```

`zc check` walks the whole chain - items registered, distraction values resolved, turnable set
populated, all seven Harmony patches applied, every online player has an owner key - and prints to
both the console and the log. Run it first if anything looks wrong.

---

## Install

Drop the `ChaoticZombieCompanion` folder into your server's `Mods/` folder and restart.

**EAC must be off.** This is a Harmony DLL mod (`SkipWithAntiCheat=true`), so it is skipped
entirely while EAC is on. Players do **not** need to install anything.

Settings live in `ChaoticZombieCompanion.cfg` beside `ModInfo.xml` - every value is commented.

---

## Why zombies cannot fight zombies

Worth writing down, because it is not one problem but two, and fixing either alone gets you nothing.

**1. A zombie will not chase another zombie.** `EAIApproachAndAttackTarget.CanExecute` walks its own
`targetClasses` list and returns false for any target whose Type is not in it. A vanilla zombie's
list is `EntityPlayer, EntityBandit, EntityEnemyAnimal, EntityAnimal` - no zombie anywhere in it. So
a zombie *holding a thrall as its attack target* will not take a single step towards it. This mod
appends `EntityEnemy` to that list, lazily, on the individual zombies that meet a thrall. One entry
covers every zombie, bandit and enemy animal, and it structurally cannot catch a player, because
`EntityPlayer` descends from `EntityAlive` directly rather than through `EntityEnemy`.

**2. The revenge channel is hard-blocked between things of the same kind.**
`EAISetAsTargetIfHurt.CanExecute` opens with `revengeTarget.entityType != theEntity.entityType`, and
`EntityType` has exactly five values - `Unknown, Player, Zombie, Animal, Bandit`. Every zombie in
the game is `EntityType.Zombie`, so `SetRevengeTarget` from one zombie to another is silently inert.
No amount of AI-list editing fixes that: it is a type check, not a list.

So aggro is held by the mod instead. A taunted zombie is recorded with an expiry, and the mod's gate
on `EntityAlive.SetAttackTarget` refuses to hand it a player while that window is open - which is
necessary because `EAISetNearestEntityAsTarget` re-scans on its own schedule and would otherwise put
it straight back on you a second later. The window refreshes every second while the zombie is still
near the thrall.

(The if-hurt list still gets `EntityEnemy` added, and that is not dead code: a bandit or an enemy
animal is a *different* `entityType` from a zombie thrall, so revenge works normally for those.)

---

## Notes for server owners

- **Thralls are never written to the save.** They are spawned as `StaticSpawner` so the horde
  manager cannot cull them or count them against its budget, which would otherwise mean they get
  written into their chunk and load on the next restart as ordinary hostile zombies standing in
  someone's base. The mod's own roster file (`ChaoticZombieCompanionThralls.tsv`, in the save
  directory) is the single authority, and thralls are re-created from it.
- **`ThrallsDrawAggro=false`** makes thralls much stronger, not weaker - nothing will fight them.
- **`ThrallTauntShare`** is the blood-moon balance dial. 1.0 hands the whole horde to your thralls.
- **`DecayMinutes`** turns thralls into a consumable with a running cost. Off by default.
- **`MaxActiveThralls`** is the performance lever. Every thrall is a live zombie entity with
  pathfinding; ten players with two each is twenty extra zombies pathing at all times.

Made by Chaotic. Sister mod: **Chaotic's Beastmaster**, which does the same thing for living
animals - wolves, bears, coyotes and mountain lions.
