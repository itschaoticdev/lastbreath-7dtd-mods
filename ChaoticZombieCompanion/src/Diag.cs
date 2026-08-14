using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace ChaoticZombieCompanion
{
    /// <summary>
    /// Self-check and live inspection.
    ///
    /// The conversion chain is six links long (item registered -> passive effects resolve -> tag on
    /// the zombie -> AI task present -> distraction claimed -> meal finished) and every one of them
    /// fails the same silent way: the zombie keeps coming for you. Without this there is no telling
    /// a bad install from a bad patch from a bad tag - which is the hole the first field report on
    /// Beastmaster fell into, and the reason this mod ships the diagnostic on day one instead of
    /// three releases in.
    ///
    /// Runs unconditionally at startup; `zc` exposes the same data live.
    /// </summary>
    public static class Diag
    {
        public static readonly string[] BaitItems = { "chaoticGoreBait", "chaoticGoreBaitRipe" };

        /// <summary>
        /// Everything that has to be true before a single zombie can be baited, checked against the
        /// running game rather than against what the XML says. Logged at WRN when a link is broken
        /// so it survives a casual look at the log.
        /// </summary>
        public static void RunStartupCheck()
        {
            bool ok;
            string report = BuildReport(out ok);
            if (ok) Log.Out(report);
            else Log.Warning(report);
        }

        /// <summary>
        /// The report as text, so `zc check` can put it in front of whoever is standing in the
        /// world rather than only in a log file they have to go and find.
        /// </summary>
        public static string BuildReport(out bool allOk)
        {
            var sb = new StringBuilder();
            bool ok = true;

            sb.Append("[ZombieCompanion] self-check\n");

            // --- link 1+2: items registered, and their distraction numbers actually resolve.
            // A passive_effect that does not parse leaves the value at 0, and a bait with
            // DistractionEatTicks 0 is deleted by EntityItem.OnUpdateEntity the frame it lands.
            foreach (string name in BaitItems)
            {
                ItemClass ic = ItemClass.GetItemClass(name);
                if (ic == null)
                {
                    sb.Append("  ITEM " + name + ": MISSING - items.xml did not load\n");
                    ok = false;
                    continue;
                }

                ItemValue iv = new ItemValue(ic.Id);
                float radius = EffectManager.GetValue(PassiveEffects.DistractionRadius, iv);
                float lifetime = EffectManager.GetValue(PassiveEffects.DistractionLifetime, iv);
                float eatTicks = EffectManager.GetValue(PassiveEffects.DistractionEatTicks, iv);
                float strength = EffectManager.GetValue(PassiveEffects.DistractionStrength, iv);

                bool tagged = ic.ItemTags.Test_AnySet(FastTags<TagGroup.Global>.GetTag("chaoticGoreBait"));
                bool itemOk = tagged && ic.IsEatDistraction && radius > 0f && lifetime > 0f && eatTicks > 0f;
                if (!itemOk) ok = false;

                sb.Append("  ITEM " + name + ": " + (itemOk ? "ok" : "BROKEN")
                    + " tag=" + tagged
                    + " eat=" + ic.IsEatDistraction
                    + " contact=" + ic.IsRequireContactDistraction
                    + " radius=" + radius + " lifetime=" + lifetime
                    + " eatTicks=" + eatTicks + " strength=" + strength + "\n");
            }

            // --- link 3: the turnable set. Applied from code, so an empty set means the naming
            // rule found nothing - i.e. this install has no entity classes called zombie*, which
            // would be very odd and is worth shouting about.
            ThrallRuntime.EnsureTagsPublic();
            var names = ThrallRuntime.Thrallable;
            if (names.Count == 0) ok = false;
            sb.Append("  TURNABLE: " + names.Count + " class(es)"
                + (names.Count > 0 ? " e.g. " + Sample(names) : " - NOTHING CAN BE TURNED") + "\n");
            sb.Append("  EXCLUDED: "
                + (string.IsNullOrEmpty(ZcConfig.ExcludeZombies) ? "(nothing)" : ZcConfig.ExcludeZombies) + "\n");

            // --- link 4: the patches are live. PatchAll is all-or-nothing today, but a future game
            // build could drop one of these methods and this says which.
            var patched = new HashSet<string>();
            foreach (var m in Harmony.GetAllPatchedMethods())
            {
                if (m != null && m.DeclaringType != null)
                    patched.Add(m.DeclaringType.Name + "." + m.Name);
            }
            string[] needed =
            {
                "EAIApproachDistraction.CanExecute",
                "EAIApproachDistraction.Update",
                "EntityAlive.SetAttackTarget",
                "EAIManager.CopyPropertiesFromEntityClass",
                // Not optional: without it a thrall is written into the chunk and comes back after
                // a restart as a hostile zombie standing in the player's base.
                "EntityEnemy.IsSavedToFile",
                "EntityAlive.DamageEntity",
                // Conversion can be perfect and the mod still look half-broken if /thrall never
                // reaches it, which is what happened to Beastmaster on client-hosted games.
                "GameManager.ChatMessageServer"
            };
            foreach (string n in needed)
            {
                bool has = patched.Contains(n);
                if (!has) ok = false;
                sb.Append("  PATCH " + n + ": " + (has ? "ok" : "NOT APPLIED") + "\n");
            }

            // --- link 5: every player in the world has an identity to hang a roster off. No owner
            // key means /thrall answers "could not identify your account" and nothing persists.
            var players = GameManager.Instance != null && GameManager.Instance.World != null
                ? GameManager.Instance.World.Players : null;
            if (players != null && players.list != null && players.list.Count > 0)
            {
                for (int i = 0; i < players.list.Count; i++)
                {
                    EntityPlayer p = players.list[i];
                    if (p == null) continue;
                    string key = ThrallRuntime.OwnerKeyOf(p);
                    bool keyed = !string.IsNullOrEmpty(key);
                    if (!keyed) ok = false;
                    sb.Append("  PLAYER " + p.EntityName + " #" + p.entityId + ": "
                        + (keyed ? "ok key=" + key + " roster=" + ThrallStore.Count(key)
                                 : "NO OWNER KEY - /thrall and persistence are dead")
                        + "\n");
                }
            }

            sb.Append("  => " + (ok ? "all links ok" : "SOMETHING IS BROKEN - conversion will not work"));

            allOk = ok;
            return sb.ToString();
        }

        private static string Sample(List<string> names)
        {
            int n = Mathf.Min(3, names.Count);
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(names[i]);
            }
            if (names.Count > n) sb.Append(", ...");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ live inspection

        public static string DescribeZombie(EntityAlive a)
        {
            EAIApproachDistraction task = null;
            if (a.aiManager != null && a.aiManager.tasks != null)
                task = a.aiManager.tasks.GetTask<EAIApproachDistraction>();

            return string.Format("  {0} #{1} turnable={2} needs={3} thrall={4} hasTask={5} target={6} pending={7} eating={8}",
                ThrallRuntime.EntityClassNameOf(a),
                a.entityId,
                ThrallRuntime.IsThrallable(a),
                ThrallRuntime.FeedsNeededFor(a),
                ThrallRuntime.IsThrall(a),
                task != null,
                a.GetAttackTarget() != null ? a.GetAttackTarget().EntityName : "-",
                a.pendingDistraction != null ? a.pendingDistraction.entityId.ToString() : "-",
                a.distraction != null ? a.distraction.entityId.ToString() : "-");
        }

        public static string DescribeBait(EntityItem it)
        {
            return string.Format("  gore #{0} {1} isBait={2} lifetime={3} eatTicks={4} radiusSq={5} collided={6} owner={7}",
                it.entityId,
                it.itemClass != null ? it.itemClass.Name : "?",
                ThrallRuntime.IsBaitItem(it),
                it.distractionLifetime,
                it.distractionEatTicks,
                it.distractionRadiusSq,
                it.isCollided,
                it.belongsPlayerId);
        }
    }

    /// <summary>
    /// `zc` - the same checks Diag runs at startup, plus live state, without a restart.
    /// Note the overrides are public, not protected: TFP ship a publicized Assembly-CSharp.
    /// </summary>
    public class ConsoleCmdZombieCompanion : ConsoleCmdAbstract
    {
        public override string[] getCommands()
        {
            return new string[] { "zc", "zombiecompanion" };
        }

        public override string getDescription()
        {
            return "Chaotic's Zombie Companion diagnostics";
        }

        public override string getHelp()
        {
            return "zc check         - re-run the startup self-check\n"
                 + "zc scan [radius] - live zombies: turnable, AI task, target, distraction (default 60m from each player)\n"
                 + "zc items         - gore bait lying in the world and its distraction state\n"
                 + "zc thralls       - every live thrall and who owns it\n"
                 + "zc list          - every entity class this mod is willing to turn\n"
                 + "zc thrall <verb> [n] [player] - run a /thrall command for a player (yourself if not named)\n"
                 + "zc trace on|off  - verbose per-decision logging, no restart needed";
        }

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            var console = SingletonMonoBehaviour<SdtdConsole>.Instance;
            string sub = _params.Count > 0 ? _params[0].ToLowerInvariant() : "check";

            var world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null && sub != "trace" && sub != "list")
            {
                console.Output("No world loaded.");
                return;
            }

            switch (sub)
            {
                case "check":
                {
                    bool ok;
                    string report = Diag.BuildReport(out ok);
                    // To the console as well as the log: whoever runs this is usually the person
                    // reporting the bug, and they should not have to go and find a log file.
                    foreach (string line in report.Split('\n')) console.Output(line);
                    if (ok) Log.Out(report); else Log.Warning(report);
                    return;
                }

                case "trace":
                    ZcConfig.Debug = _params.Count > 1 && _params[1].ToLowerInvariant() == "on";
                    console.Output("ZombieCompanion trace " + (ZcConfig.Debug ? "ON" : "OFF"));
                    return;

                case "list":
                {
                    var names = ThrallRuntime.Thrallable;
                    for (int i = 0; i < names.Count; i++) console.Output("  " + names[i]);
                    console.Output(names.Count + " turnable class(es).");
                    return;
                }

                case "thralls":
                {
                    int n = 0;
                    foreach (var kv in ThrallRuntime.LiveThralls)
                    {
                        var e = world.GetEntity(kv.Key) as EntityAlive;
                        console.Output(string.Format("  #{0} {1} owner={2} hp={3} target={4}",
                            kv.Key, kv.Value.EntityClassName, kv.Value.OwnerKey,
                            e != null ? e.Health.ToString() : "-",
                            e != null && e.GetAttackTarget() != null ? e.GetAttackTarget().EntityName : "-"));
                        n++;
                    }
                    console.Output(n + " live thrall(s), " + ThrallRuntime.TauntedCount
                        + " hostile(s) currently held on one.");
                    return;
                }

                case "thrall":
                {
                    // A way in that does not go through chat at all. Chat is one line of code away
                    // from being owned by another mod, and when it breaks the roster becomes
                    // unreachable - a player with thralls out has no way to put them away.
                    EntityPlayer target = ResolvePlayer(_params.Count > 3 ? _params[3] : null, _senderInfo, world);
                    if (target == null)
                    {
                        console.Output("No such player. Usage: zc thrall <verb> [n] [player name or entity id]");
                        return;
                    }
                    console.Output("Running '" + string.Join(" ", _params.ToArray(), 1, _params.Count - 1)
                        + "' for " + target.EntityName + ".");
                    // Mirror the answer into the console. Whoever typed this is usually on telnet
                    // with no chat window to read the reply in.
                    ThrallCommands.Echo = line => { foreach (string l in line.Split('\n')) console.Output(l); };
                    try { ThrallCommands.RunFor(target, _params.ToArray(), 1); }
                    finally { ThrallCommands.Echo = null; }
                    return;
                }

                case "items":
                {
                    int n = 0;
                    foreach (Entity e in world.Entities.list)
                    {
                        EntityItem it = e as EntityItem;
                        if (it == null || !ThrallRuntime.IsBaitItem(it)) continue;
                        console.Output(Diag.DescribeBait(it));
                        n++;
                    }
                    console.Output(n + " gore bait item(s) in the world.");
                    return;
                }

                case "scan":
                {
                    float radius = 60f;
                    if (_params.Count > 1) float.TryParse(_params[1], out radius);
                    float radiusSq = radius * radius;

                    var players = world.Players != null ? world.Players.list : null;
                    int n = 0;
                    // Counted so a zero can be read. "0 turnable zombies" on its own cannot tell an
                    // empty chunk from a tagging failure, and that ambiguity is what got the last
                    // report on the sister mod stuck.
                    int alive = 0, untagged = 0;
                    foreach (Entity e in world.Entities.list)
                    {
                        EntityAlive a = e as EntityAlive;
                        if (a == null || a is EntityPlayer || a.IsDead()) continue;
                        alive++;
                        if (!ThrallRuntime.IsLurable(a))
                        {
                            if (a is EntityEnemy) untagged++;
                            continue;
                        }

                        bool near = players == null || players.Count == 0;
                        if (players != null)
                        {
                            for (int i = 0; i < players.Count; i++)
                            {
                                if (a.GetDistanceSq(players[i]) <= radiusSq) { near = true; break; }
                            }
                        }
                        if (!near) continue;

                        console.Output(Diag.DescribeZombie(a));
                        n++;
                    }
                    console.Output(n + " lurable zombie(s) within " + radius + "m of a player"
                        + " (" + alive + " living entities loaded, " + untagged
                        + " hostile(s) with no zombie tag). Players online: "
                        + (players != null ? players.Count : 0) + ".");
                    if (n == 0 && untagged > 0)
                    {
                        console.Output("Hostiles are loaded but carry no zombie tag - run 'zc check'.");
                    }
                    else if (n == 0 && alive == 0)
                    {
                        console.Output("Nothing is loaded at all - no chunks are active, so there is "
                            + "nothing to scan. Stand next to a zombie and run this again.");
                    }
                    return;
                }

                default:
                    console.Output(getHelp());
                    return;
            }
        }

        /// <summary>
        /// Names the player a `zc thrall` call is for: an explicit name or entity id, else whoever
        /// typed it (their own client on a dedicated server, the local player on a hosted game).
        /// </summary>
        private static EntityPlayer ResolvePlayer(string who, CommandSenderInfo sender, World world)
        {
            var players = world.Players != null ? world.Players.list : null;
            if (players == null) return null;

            if (!string.IsNullOrEmpty(who))
            {
                int wantId;
                if (int.TryParse(who, out wantId))
                {
                    for (int i = 0; i < players.Count; i++)
                        if (players[i] != null && players[i].entityId == wantId) return players[i];
                }
                for (int i = 0; i < players.Count; i++)
                {
                    if (players[i] != null && string.Equals(players[i].EntityName, who,
                        StringComparison.OrdinalIgnoreCase)) return players[i];
                }
                return null;
            }

            if (sender.RemoteClientInfo != null)
                return world.GetEntity(sender.RemoteClientInfo.entityId) as EntityPlayer;

            // Local console on a hosted game: there is exactly one player it can mean.
            return GameManager.Instance.World.GetPrimaryPlayer();
        }
    }
}
