# Chaotic's Beastmaster

**Throw meat. Walk away. Feed it five times and it walks away with you.**

A wolf pack catching you in the open is one of the few things in 7 Days to Die you simply cannot
outrun. This gives you an answer: a slab of bloody bait you can throw. The animal breaks off,
goes to the meat, and puts its head down long enough for you to leave.

Do it five times to the same animal and it stops being a predator. It follows you, it fights
zombies for you, and it will never turn on a player again.

---

## What players do

| | |
|---|---|
| **Craft** | **Chaotic's Bloody Bait** - 10 Raw Meat, 2 Animal Fat, 2 Bone. No workstation, available from day one, makes 2. |
| **Throw** | Hold to aim, release to throw, same as a rock. It only takes effect once it lands. |
| **Escape** | The **nearest** wolf, coyote, mountain lion or bear drops what it is doing - including chasing you - and goes to eat it. |
| **Tame** | Every finished meal counts. Five meals and the animal is yours. |
| **Upgrade** | **Chaotic's Prime Bait** - 2 Bloody Bait, 5 Animal Fat, 1 Testosterone Extract, at a campfire with a cooking pot. Pulls from 45m instead of 30m and counts as **two** meals. This is how you tame a bear without standing next to a grizzly five separate times. |

Bait fed to an animal that is **already** yours heals it instead - 25% of its health for Bloody,
50% for Prime.

**One slab, one animal.** A thrown bait occupies exactly one beast - the closest one that can
reach it. A wolf pack will *not* all stop for the same piece of meat, so a pack costs you a
throw per wolf. Without this the whole pack peels off one slab and, since a feed is scored per
bait, only one of them would have got credit anyway.

## Your kennel - one out at a time

You can own several companions but only ever have **one out**. Commands are in chat, so nothing
to install:

| Command | |
|---|---|
| `/pet list` | what you own, and which one is out |
| `/pet call <n>` | bring that one out - whatever is out now goes away |
| `/pet stow` | put your companion away |
| `/pet release <n>` | set one free permanently |

`/pets`, `/beast` and `/bm` all work as the same command if `/pet` collides with another mod.
Admins also have `bm pet <verb> [n] [player]` in the server console, which does the same thing
without going through chat at all.

Taming a new animal while one is already at your side automatically puts the old one in the
kennel and the new one takes its place. If the kennel is full (5 by default) the animal will not
follow you until you release one.

## What it works on

Being able to **escape** something and being able to **keep** it are separate questions, so there
are two tags:

| | Bait works | Can be tamed | |
|---|---|---|---|
| Wolf, Coyote, Mountain Lion, Bear, Small Bear | yes | **yes** | `chaoticTameable` |
| Dire Wolf, Zombie Bear, Zombie Dog, Zombie Boar | yes | no | `chaoticBaitable` |
| Everything else (zombies, stags, chickens) | no | no | untagged |

The undead animals are the ones that actually kill you, so bait has to buy you a way out of one -
but a pet zombie bear would trivialise a blood moon. They eat, they stop chasing, and that is the
end of it. Feeding one just tells you so.

Moving an animal between rows is a one-line edit in `Config/entityclasses.xml` - swap which tag it
gets. No code change.

(Zombie Dog and Zombie Boar have no `ApproachDistraction` AI task in vanilla at all; the DLL gives
them one, which is why bait works on them here and a vanilla rock does not.)

## Your companion

- Follows you, and teleports to you past 45m so you cannot lose it to a vehicle.
- Fights zombies and bandits. Uses the animal's own vanilla combat AI, so a bear still hits like
  a bear.
- Will not attack players, including you. Shoot it by accident and it does not care.
- Does not flee at low health, wander off, or return to a territory - those AI tasks are stripped.
- +50% max health, and it heals to full the moment it is tamed.
- Survives relogs and server restarts. It is stored away when you log out and falls in beside you
  when you come back.
- If it dies, it is gone - it is a real animal, not a respawning pet.

## Installing

Server-side only. Drop the `ChaoticBeastmaster` folder in `Mods/` and restart. Players need
nothing: the bait reuses the vanilla raw-meat model and icon, so there is no download and no
version mismatch.

Requires the game's bundled Harmony (`Mods/0_TFP_Harmony`), which ships with 7 Days to Die V2+.

## Configuring

`ChaoticBeastmaster.cfg` sits next to the DLL. Every value has a comment. The ones worth knowing:

| Setting | Default | |
|---|---|---|
| `FeedsToTame` | 5 | Meals before an animal turns. |
| `PrimeBaitFeedValue` | 2 | What one Prime Bait is worth. |
| `MaxOwnedPets` | 5 | Kennel size. Only one is ever out. `0` disables taming entirely. |
| `MaxAnimalsPerBait` | 1 | Animals one slab can occupy. `0` = vanilla (whole pack piles on). |
| `PacifySeconds` | 8 | How long a baited animal is barred from re-targeting you. |
| `BreakActiveAggro` | true | Whether bait works on something already chasing you. |
| `PetsAttackPlayers` | false | Turn on only for PvP servers. |
| `ExtraTameable` | *(empty)* | Extra entity class names to make tameable, comma separated. |
| `ExtraBaitable` | *(empty)* | Extra entity class names bait works on but that never follow you. |
| `Debug` | false | Verbose log: task injection, aggro breaks, feed counts, respawns. |

## If it is not working

Run `bm check` in the server console. It prints every link in the chain - the bait items, the
tag on each animal, the patches, and whether each player online has an identity to hang a kennel
off - and says which one is broken. `bm scan` lists the
animals loaded near a player and whether each one carries the tag; if it reports zero while you
are standing next to a bear, `bm check` will say why. `bm trace on` logs a line a second pairing
each thrown bait with the nearest animal, no restart needed.

## How it works

Most of this is vanilla machinery, re-pointed.

7 Days to Die already has a thrown-decoy system - it is how a rock pulls zombies. An `EntityItem`
with a `DistractionTags` property pulses at nearby entities and sets `pendingDistraction` on any
whose own tags match. The `ApproachDistraction` AI task then walks the entity over, and if the
item carries the `eat` tag the entity stops and eats it rather than glancing at it.

Vanilla ships exactly one use of this: rocks and snowballs, tagged `zombie`, non-eat. So:

- The bait items are tagged `chaoticTameable,eat,requires_contact` instead. That single string is
  what makes them lure animals rather than zombies, and makes the animal commit to a meal.
- Wolves and bears get the `chaoticTameable` tag added to their `Tags`. `entityclasses.xml` does
  it with a one-line `csv` xpath patch each, appending rather than replacing so it does not fight
  other mods - but the DLL also applies the same tags itself at world load, because a mod that
  loads later and *sets* an animal's `Tags` would otherwise wipe them and leave every animal in
  the world quietly deaf to bait.

Four things XML cannot do, which is what the DLL is for:

1. **The AI task.** Vanilla only gives `ApproachDistraction` to the *zombie* animals. Declaring it
   in XML for a wolf would mean restating that wolf's entire `AITask` list, which silently breaks
   every time TFP retune one. Instead it is injected at spawn, in a postfix on
   `EAIManager.CopyPropertiesFromEntityClass` - the exact moment the task lists are built.
2. **Breaking a chase.** `EAIApproachDistraction.CanExecute` refuses to run while the animal holds
   an attack target - i.e. precisely when you need the bait. A prefix drops the target first, and
   a short pacify window on `EntityAlive.SetAttackTarget` stops it re-acquiring you in the same
   frame. After that, vanilla suppresses re-targeting on its own.
3. **Counting meals.** A postfix on `EAIApproachDistraction.Update` watches `distractionEatTicks`
   hit zero, which is the game's own definition of "that food is gone".
4. **Companions.** Taming rewires the animal in place - no despawn and respawn, so the beast that
   follows you is the one you fed. Its existing `ApproachAndAttackTarget` and
   `SetNearestEntityAsTarget` task data is swapped for a hostiles-only list, the flee/wander/
   territory tasks are removed, and one genuinely new task (`EAIChaoticFollowOwner`) is added to
   heel. `SetAttackTarget` is gated so a pet is structurally incapable of turning on a player.

Persistence is the mod's own, because 7 Days to Die does not save animals at all - a wild wolf
dies with its chunk and certainly does not survive a restart. `ChaoticBeastmasterPets.tsv` in the
save folder holds one line per owner, and the companion is re-created next to them when they load
back in.

## Compatibility

- Does not replace or restate any vanilla AI list, so it survives TFP retuning animals.
- The only vanilla data touched is nine `Tags` attributes, appended to rather than replaced. Load
  order cannot break this: if another mod overwrites one, the DLL puts the tag back at world load
  and says so in the log.
- Adds no new assets, so it stays server-side.
- Patches five vanilla methods, all with postfixes or non-destructive prefixes.
- Works the same on a dedicated server, a client-hosted game and single player. Chat commands are
  read straight off `GameManager.ChatMessageServer` rather than through mod events, so neither the
  host having no `ClientInfo` nor another chat mod running first can hide them.

## Changelog

**1.2.3** - `/pet` now works on client-hosted and single-player games. The host of their own game
is not a network client of it, so their chat arrives with no `ClientInfo` attached, and the
command handler was throwing every such line away - which on those games made the entire text
command set look like it did not exist. Chat is now read from a Harmony prefix on
`ChatMessageServer` instead of from `ModEvents.ChatMessage`, so it cannot be pre-empted by another
chat mod either. Same reason the host's companions never survived a restart: their owner identity
was read from the same missing `ClientInfo`, and now falls back to the persistent player list.
Adds `/pets`, `/beast`, `/bm` aliases, the `bm pet` console command, and both new checks to
`bm check`.

**1.2.2** - animal tags applied from code as well as XML, so another mod overwriting `Tags` can no
longer make the whole mod silently do nothing. `bm check` reports per animal where its tag came
from; `bm scan` distinguishes "no chunks loaded" from "animals loaded but untagged".

**1.2.1** - startup self-check and the `bm` console command.

**1.2.0** - bait works on dire wolves, zombie bears, zombie dogs and zombie boars. They will eat
it and leave you alone; they will never follow you home.
