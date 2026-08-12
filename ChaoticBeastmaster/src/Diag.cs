using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Platform;
using UnityEngine;

namespace ChaoticBeastmaster
{
    /// <summary>
    /// Self-check and live inspection.
    ///
    /// The taming chain is six links long (item registered -> passive effects resolve -> tags on
    /// the animal -> AI task injected -> distraction claimed -> meal finished) and every one of
    /// them fails the same silent way: the animal keeps chasing you. Without this there is no way
    /// to tell a bad install from a bad patch from a bad tag, which is exactly the hole the first
    /// field bug report fell into.
    ///
    /// Runs unconditionally at startup; `bm` exposes the same data live.
    /// </summary>
    public static class Diag
    {
        public static readonly string[] BaitItems = { "chaoticBaitBloody", "chaoticBaitPrime" };

        // Single list, owned by the code that applies the tags - a diagnostic that checks a
        // different set of animals from the one the mod actually tags is worse than none.
        public static string[] TameableAnimals { get { return TameRuntime.DefaultTameable; } }
        public static string[] BaitableAnimals { get { return TameRuntime.DefaultBaitable; } }

        /// <summary>
        /// Everything that has to be true before a single wolf can be baited, checked against the
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
        /// The report as text, so `bm check` can put it in front of whoever is standing in the
        /// world rather than only in a log file they have to go and find. The first field report
        /// on this mod cost a round trip precisely because the answer was log-only.
        /// </summary>
        public static string BuildReport(out bool allOk)
        {
            var sb = new StringBuilder();
            bool ok = true;

            sb.Append("[Beastmaster] self-check\n");

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

                bool tagged = ic.ItemTags.Test_AnySet(FastTags<TagGroup.Global>.GetTag("chaoticBait"));
                bool itemOk = tagged && ic.IsEatDistraction && radius > 0f && lifetime > 0f && eatTicks > 0f;
                if (!itemOk) ok = false;

                sb.Append("  ITEM " + name + ": " + (itemOk ? "ok" : "BROKEN")
                    + " tag=" + tagged
                    + " eat=" + ic.IsEatDistraction
                    + " contact=" + ic.IsRequireContactDistraction
                    + " radius=" + radius + " lifetime=" + lifetime
                    + " eatTicks=" + eatTicks + " strength=" + strength + "\n");
            }

            // --- link 3: the tags are on the animals, and where they came from. The mod applies
            // them from code now, so "forced" is not a failure - but it does mean the xpath in
            // entityclasses.xml is being eaten in this install, which is worth saying out loud.
            TameRuntime.EnsureTagsPublic();
            int taggedCount = 0;
            foreach (string name in TameableAnimals) if (CheckAnimal(sb, name, true)) taggedCount++;
            foreach (string name in BaitableAnimals) if (CheckAnimal(sb, name, false)) taggedCount++;
            if (taggedCount != TameableAnimals.Length + BaitableAnimals.Length) ok = false;

            // --- link 4: the patches are live. PatchAll is all-or-nothing today, but a future
            // game build could drop one of these methods and this says which.
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
                // Chat is a link in its own right: taming can be perfect and the mod still looks
                // half-broken if /pet never reaches it, which is exactly what 1.2.2 did on every
                // client-hosted game.
                "GameManager.ChatMessageServer"
            };
            foreach (string n in needed)
            {
                bool has = patched.Contains(n);
                if (!has) ok = false;
                sb.Append("  PATCH " + n + ": " + (has ? "ok" : "NOT APPLIED") + "\n");
            }

            // --- link 5: every player in the world has an identity to hang a kennel off. No
            // owner key means /pet answers "could not identify your account" and nothing persists.
            var players = GameManager.Instance != null && GameManager.Instance.World != null
                ? GameManager.Instance.World.Players : null;
            if (players != null && players.list != null && players.list.Count > 0)
            {
                for (int i = 0; i < players.list.Count; i++)
                {
                    EntityPlayer p = players.list[i];
                    if (p == null) continue;
                    string key = TameRuntime.OwnerKeyOf(p);
                    bool keyed = !string.IsNullOrEmpty(key);
                    if (!keyed) ok = false;
                    sb.Append("  PLAYER " + p.EntityName + " #" + p.entityId + ": "
                        + (keyed ? "ok key=" + key : "NO OWNER KEY - /pet and persistence are dead")
                        + "\n");
                }
            }

            sb.Append("  => " + (ok ? "all links ok" : "SOMETHING IS BROKEN - taming will not work"));

            allOk = ok;
            return sb.ToString();
        }

        private static bool CheckAnimal(StringBuilder sb, string className, bool expectTameable)
        {
            // Scanned rather than looked up: EntityClass.list is keyed by name.GetHashCode(), and
            // going through EntityClass.FromString made two perfectly present animals look absent.
            EntityClass ec = null;
            foreach (var kv in EntityClass.list.Dict)
            {
                if (kv.Value != null && kv.Value.entityClassName == className) { ec = kv.Value; break; }
            }
            if (ec == null)
            {
                sb.Append("  ANIMAL " + className + ": class not found (removed by the game or another mod)\n");
                return false;
            }

            bool tameable = ec.Tags.Test_AnySet(TameRuntime.TameableTag);
            bool baitable = ec.Tags.Test_AnySet(TameRuntime.BaitableTag);
            bool want = expectTameable ? tameable : baitable;

            // Always a line, pass or fail. A check that says nothing when it passes cannot be
            // pasted into a bug report as evidence that it passed.
            string source = TameRuntime.TagCameFromXml(className) ? "from xml"
                          : TameRuntime.TagWasForced(className) ? "forced by code (xpath did not apply)"
                          : "no tag";
            sb.Append("  ANIMAL " + className + ": " + (want ? "ok" : "TAG MISSING")
                + " tameable=" + tameable + " baitable=" + baitable + " " + source + "\n");
            return want;
        }

        // ------------------------------------------------------------------ live inspection

        public static string DescribeAnimal(EntityAlive a)
        {
            EAIApproachDistraction task = null;
            if (a.aiManager != null && a.aiManager.tasks != null)
                task = a.aiManager.tasks.GetTask<EAIApproachDistraction>();

            return string.Format("  {0} #{1} tameable={2} baitable={3} hasTask={4} target={5} pending={6} eating={7}",
                TameRuntime.EntityClassNameOf(a),
                a.entityId,
                TameRuntime.IsTameable(a),
                TameRuntime.IsBaitable(a),
                task != null,
                a.GetAttackTarget() != null ? a.GetAttackTarget().EntityName : "-",
                a.pendingDistraction != null ? a.pendingDistraction.entityId.ToString() : "-",
                a.distraction != null ? a.distraction.entityId.ToString() : "-");
        }

        public static string DescribeBait(EntityItem it)
        {
            return string.Format("  bait #{0} {1} isBait={2} lifetime={3} eatTicks={4} radiusSq={5} collided={6} owner={7}",
                it.entityId,
                it.itemClass != null ? it.itemClass.Name : "?",
                TameRuntime.IsBaitItem(it),
                it.distractionLifetime,
                it.distractionEatTicks,
                it.distractionRadiusSq,
                it.isCollided,
                it.belongsPlayerId);
        }
    }

    /// <summary>
    /// `bm` - the same checks Diag runs at startup, plus live state, without a restart.
    /// Note the overrides are public, not protected: TFP ship a publicized Assembly-CSharp.
    /// </summary>
    public class ConsoleCmdBeastmaster : ConsoleCmdAbstract
    {
        public override string[] getCommands()
        {
            return new string[] { "bm", "beastmaster" };
        }

        public override string getDescription()
        {
            return "Chaotic's Beastmaster diagnostics";
        }

        public override string getHelp()
        {
            return "bm check         - re-run the startup self-check\n"
                 + "bm scan [radius] - live animals: tags, AI task, target, distraction (default 60m from each player)\n"
                 + "bm items         - bait lying in the world and its distraction state\n"
                 + "bm pet <verb> [n] [player] - run a /pet command for a player (yourself if not named)\n"
                 + "bm trace on|off  - verbose per-decision logging, no restart needed";
        }

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            var console = SingletonMonoBehaviour<SdtdConsole>.Instance;
            string sub = _params.Count > 0 ? _params[0].ToLowerInvariant() : "check";

            var world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null && sub != "trace")
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
                    BmConfig.Debug = _params.Count > 1 && _params[1].ToLowerInvariant() == "on";
                    console.Output("Beastmaster trace " + (BmConfig.Debug ? "ON" : "OFF"));
                    return;

                case "pet":
                {
                    // A way in that does not go through chat at all. Chat is one line of code away
                    // from being owned by another mod, and when it breaks the kennel becomes
                    // unreachable - a player with a companion out has no way to put it away.
                    EntityPlayer target = ResolvePlayer(_params.Count > 3 ? _params[3] : null, _senderInfo, world);
                    if (target == null)
                    {
                        console.Output("No such player. Usage: bm pet <verb> [n] [player name or entity id]");
                        return;
                    }
                    console.Output("Running '" + string.Join(" ", _params.ToArray(), 1, _params.Count - 1)
                        + "' for " + target.EntityName + ".");
                    // Mirror the answer into the console. Whoever typed this is usually on telnet
                    // with no chat window to read the reply in.
                    PetCommands.Echo = line => { foreach (string l in line.Split('\n')) console.Output(l); };
                    try { PetCommands.RunFor(target, _params.ToArray(), 1); }
                    finally { PetCommands.Echo = null; }
                    return;
                }

                case "items":
                {
                    int n = 0;
                    foreach (Entity e in world.Entities.list)
                    {
                        EntityItem it = e as EntityItem;
                        if (it == null || !TameRuntime.IsBaitItem(it)) continue;
                        console.Output(Diag.DescribeBait(it));
                        n++;
                    }
                    console.Output(n + " bait item(s) in the world.");
                    return;
                }

                case "scan":
                {
                    float radius = 60f;
                    if (_params.Count > 1) float.TryParse(_params[1], out radius);
                    float radiusSq = radius * radius;

                    var players = world.Players != null ? world.Players.list : null;
                    int n = 0;
                    // Counted so a zero can be read. "0 baitable animals" on its own cannot tell
                    // an empty chunk from a tagging failure, and that ambiguity is what the last
                    // bug report got stuck on.
                    int alive = 0, untagged = 0;
                    foreach (Entity e in world.Entities.list)
                    {
                        EntityAlive a = e as EntityAlive;
                        if (a == null || a is EntityPlayer || a.IsDead()) continue;
                        alive++;
                        if (!TameRuntime.IsBaitable(a))
                        {
                            if (TameRuntime.EntityClassNameOf(a).StartsWith("animal")) untagged++;
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

                        console.Output(Diag.DescribeAnimal(a));
                        n++;
                    }
                    console.Output(n + " baitable animal(s) within " + radius + "m of a player"
                        + " (" + alive + " living entities loaded, " + untagged
                        + " untagged animal(s)). Players online: "
                        + (players != null ? players.Count : 0) + ".");
                    if (n == 0 && untagged > 0)
                    {
                        console.Output("Animals are loaded but carry no bait tag - run 'bm check'.");
                    }
                    else if (n == 0 && alive == 0)
                    {
                        console.Output("Nothing is loaded at all - no chunks are active, so there is "
                            + "nothing to scan. Stand next to an animal and run this again.");
                    }
                    return;
                }

                default:
                    console.Output(getHelp());
                    return;
            }
        }

        /// <summary>
        /// Names the player a `bm pet` call is for: an explicit name or entity id, else whoever
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