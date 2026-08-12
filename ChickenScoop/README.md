# Chicken Scoop

**For 7 Days to Die V3.1**

Fit the vanilla **Vehicle Plow Mod** to a 4x4 Truck, then drive over chickens to scoop them up
**alive** into the truck's storage. Haul a full load back to base and drop them straight into a
chicken coop.

No more parking, chasing a chicken on foot, and grabbing them one at a time.

---

## How it works

The plow **is** the scoop. `modVehiclePlow` is only installable on the 4x4, and the truck already
has a plow blade built into its model — so when you fit the mod, you get a real blade on the front
bumper. No custom models, no reskins, nothing for your players to download.

Birds are scooped **alive**, not killed. They arrive in the truck as the game's own `wildChicken`
item, keeping all the vanilla behaviour: stress, freakout timers, coop conversion. Drag one from
the truck into a chicken coop and it converts to a domesticated chicken exactly as if you had
carried it there by hand.

## Requirements

- 7 Days to Die **V3.1**
- **EAC must be OFF.** This is a Harmony code mod. `SkipWithAntiCheat` is set, so with EAC on the
  mod is simply skipped and nothing breaks — it just does nothing.
- Works in singleplayer and on dedicated servers.

## Installation

Drop the `ChickenScoop` folder into your `Mods` folder:

- **Dedicated server:** `<server dir>/Mods/ChickenScoop`
- **Singleplayer / client:** `%APPDATA%/Roaming/7DaysToDie/Mods/ChickenScoop`

Restart the game or server.

**On a dedicated server, only the server needs this mod.** All the logic is server-authoritative
and the item it uses is vanilla, so players do not need to install anything.

## Usage

1. Fit a **Vehicle Plow Mod** to a 4x4 Truck.
2. Fit a **Vehicle Storage Mod** to the same truck — this is where the chickens go.
3. Drive over chickens at any reasonable speed.
4. Drive home, open the truck's storage, and drag chickens into a chicken coop.

Note the 4x4 has 5 mod slots and 6 possible mods, so a plow + storage build gives up one slot
compared to a fully kitted truck. If you would rather skip that tradeoff, see `RequirePlowMod`
below.

## Configuration

Edit `ChickenScoop.cfg` in the mod folder and restart.

| Setting | Default | What it does |
| --- | --- | --- |
| `Enabled` | `true` | Master switch. `false` applies no patches at all. |
| `RequirePlowMod` | `true` | Require the plow. Set `false` to let any vehicle with storage scoop. |
| `ScoopModTag` | `plow` | Vehicle mod tag that counts as a scoop. |
| `TargetEntityTags` | `chicken` | Entity tags scanned. Try `animal` to widen it. |
| `ScanForwardOffset` | `1.6` | Metres ahead of the vehicle to place the scan box. |
| `ScanRadius` | `2.2` | Half-width of the scan box, in metres. |
| `MinSpeed` | `2.0` | Minimum speed to scoop, in metres/second. |
| `ScanInterval` | `0.15` | Seconds between scans. Lower catches birds at higher speed. |
| `MaxPerScan` | `2` | Max birds per scan, so a flock does not vanish at once. |
| `PlaySound` | `true` | Play the chicken pickup sound. |
| `NotifyDriver` | `true` | Show the driver a tooltip. |
| `Debug` | `false` | Log speed, storage slots and birds found per scan. For troubleshooting. |

**If chickens get run over instead of scooped,** lower `ScanInterval` or raise `ScanRadius`.

## Modded animals

Scoopability is read from each entity class's vanilla `PickupItem` property rather than being
hardcoded to chickens. Any modded animal the game already lets you pick up by hand will work
automatically — just add its tag to `TargetEntityTags`.

## Compatibility

- Adds no new items, blocks, or recipes.
- Touches exactly one vanilla XML value: it appends `Vehicle,Backpack` to `wildChicken`'s
  `RestrictedMove` so a live chicken is allowed to sit in vehicle storage. `Backpack` has to be in
  that list too — vehicle storage is a `Bag`, and `Bag.AddItem` refuses anything that cannot also
  go in a backpack — so as a side effect live chickens can now be carried in your inventory.
- The only code hook is a postfix on `EntityVehicle.Update`, throttled per vehicle and gated on
  server authority.

## Changelog

### 1.0.2

- **Fixed: nothing was scooped on a dedicated server** — chickens were simply run over and killed.
  Singleplayer was unaffected. On a dedicated server every player entity is flagged
  `isEntityRemote`, and the game copies that flag onto a vehicle the moment a player takes the
  driver's seat, because the driving client simulates the physics. The mod treated that flag as
  "another machine owns this" and bailed out. Server authority is now tested with `IsServer` alone,
  which is what actually decides who owns the world.
- Speed and heading are now measured from how far the vehicle moved between scans instead of from
  its rigidbody. A player-driven vehicle's rigidbody is forced kinematic on the server and reads
  zero velocity, so the old speed check could never pass there either.
- The scan now sweeps the ground covered since the previous scan rather than checking one box at
  the current position, so a fast truck cannot skip a bird between two scans.
- Added a `Debug` config option that logs why a moving vehicle did or did not scoop.

### 1.0.1

- **Fixed: "Vehicle storage is full" on every scoop, even in an empty truck.** Vehicle storage is a
  `Bag`, and `Bag.AddItem` refuses any item that cannot also go in a backpack. `wildChicken` was
  given `Vehicle` but not `Backpack`, so no chicken could ever be stored. Both are now granted.
- The mod now logs a clear warning naming the item if a pickup item is ever blocked this way,
  instead of reporting it to the driver as full storage.
- Fixed the release archive, which was packed with backslash path separators and so extracted
  without its folder structure.

### 1.0.0

- Initial release.

## Credits

Uses the game's own plow attachment, chicken pickup system, and coop conversion. Built against
V3.1.0 (b14).
