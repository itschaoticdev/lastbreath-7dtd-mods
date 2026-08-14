# Persistent Parties

**7 Days to Die V3.x — server-side only, no client install.**

Your party survives a relog. Log back in and you are put straight back into your
group, instead of everyone re-inviting each other every single session.

---

## The problem

Vanilla parties live only in memory. Disconnect and you are out of the group. Come
back and someone has to re-invite everybody, every time. Shared quests, shared XP,
map markers, party colours — all of it has to be rebuilt from scratch after each
restart or crash.

It is a long-standing request, and the usual answer has been that it probably can't
be modded. It can.

## What this does

- Remembers who was in a party, keyed to your **platform account** rather than the
  temporary entity id the game reassigns each session (that reassignment is the
  actual reason vanilla can't do this).
- When you log back in, puts you back into your party automatically.
- Leaving or being kicked is still permanent — only disconnects are forgiven.
- Optional chat line to the party when someone is restored.

It re-joins you through the game's **own** server-side join path, so the party UI,
colours, shared quests and voice all update exactly as if someone had invited you.
There is no custom netcode in this mod at all.

---

## Install

Drop the `PersistentParties` folder into your server's `Mods` folder and restart:

```
7DaysToDieServer/Mods/PersistentParties/
```

**Requires EAC to be off** (`EACEnabled=false` in serverconfig.xml). That applies to
every C# mod in 7 Days to Die, not just this one. Players do **not** need to install
anything, and clients can keep EAC on.

---

## Configuration

`PersistentParties.cfg`, written on first start. Restart to apply changes.

| Setting | Default | What it does |
| --- | --- | --- |
| `enabled` | `true` | Master switch. `false` = loads but does nothing. |
| `restore_delay_seconds` | `3.0` | Wait after spawn before restoring. The client needs a moment to finish loading or the party panel can miss the update. |
| `announce` | `true` | Chat line to the party when someone is restored. |
| `forget_after_days` | `0` | Forget a party nobody has logged into for this many days. `0` = never. |
| `debug_log` | `false` | Logs why each restore did or didn't happen. |

**If players rejoin but see an empty party list, raise `restore_delay_seconds`** to
5–8. Slower machines and bigger worlds take longer to finish loading in.

---

## How it decides

- Party membership is saved when someone **joins** a party.
- It is erased when someone **leaves** or is **kicked**.
- It is deliberately kept when someone **disconnects** — that's the whole point.
- Restoring needs two members of the saved party online. Whoever logs in second is
  the one who triggers the group re-forming; the first person to log in just waits.
- If an online member has since joined a *different* party, you won't be dragged into
  it. The mod only re-forms a group out of its own members.

Data lives in `Mods/PersistentParties/<GameName>.parties.dat`, one file per world, so
two saves on one box never share parties. It's a plain text file you can read.

---

## Limits worth knowing

- **Party size is still 8.** That cap is hardcoded in the game, not in this mod. If
  your group is bigger than 8, the extras can't be restored and you'll see a warning
  in the log.
- **Crafting quality, land claims and other systems are untouched.** This restores
  party membership, nothing else.
- Safe to add or remove mid-playthrough. Removing it just means parties stop
  persisting again; nothing in your save is modified.

## Compatibility

Built and tested on **V 3.1.0 (b14)** alongside 40 other mods. Harmony patches are
narrow — three vanilla party methods — so conflicts are unlikely. Pairs with
**Offline Raid Protection**, which can read this mod's saved parties so a clan can't
leave one member offline to keep their base invulnerable.
