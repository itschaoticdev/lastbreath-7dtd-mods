using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ChaoticZombieCompanion
{
    /// <summary>State the mod keeps about one zombie that is part-way through being turned.</summary>
    public class BaitState
    {
        /// <summary>Entity id of the player whose gore this zombie is working on.</summary>
        public int FeederEntityId = -1;
        /// <summary>Time.time before which this zombie may not target a player.</summary>
        public float PacifyUntil;
        /// <summary>Finished meals so far, in Ripe-adjusted units.</summary>
        public int Feeds;
    }

    /// <summary>Who is currently working on one bait item. zombie entity id -> last seen time.</summary>
    public class BaitClaim
    {
        public readonly Dictionary<int, float> Zombies = new Dictionary<int, float>();
    }

    /// <summary>A turned zombie currently standing in the world.</summary>
    public class ThrallState
    {
        public int ThrallEntityId;
        public int OwnerEntityId = -1;
        /// <summary>Stable cross-session owner key (PlatformUserIdentifierAbs.CombinedString).</summary>
        public string OwnerKey;
        public string EntityClassName;
        /// <summary>Time.time it was turned or last respawned, for DecayMinutes.</summary>
        public float BoundAt;
    }

    public static class ThrallRuntime
    {
        /// <summary>Tag every bait item carries (items.xml). Cheaper than comparing item names.</summary>
        private static FastTags<TagGroup.Global> baitItemTag;
        /// <summary>Tag this mod puts on the zombies it is willing to turn.</summary>
        private static FastTags<TagGroup.Global> thrallTag;
        /// <summary>Vanilla's own tag. Every zombie has it, and the bait's DistractionTags match it.</summary>
        private static FastTags<TagGroup.Global> vanillaZombieTag;
        private static bool tagsReady;

        /// <summary>Zombies part-way through turning, keyed by zombie entity id.</summary>
        private static readonly Dictionary<int, BaitState> Baited = new Dictionary<int, BaitState>();

        /// <summary>Live thralls, keyed by zombie entity id.</summary>
        private static readonly Dictionary<int, ThrallState> Thralls = new Dictionary<int, ThrallState>();

        /// <summary>Bait items already scored, so one pile cannot count twice.</summary>
        private static readonly HashSet<int> ConsumedBait = new HashSet<int>();

        /// <summary>Which zombies have laid claim to a given bait item, keyed by bait entity id.</summary>
        private static readonly Dictionary<int, BaitClaim> BaitClaims = new Dictionary<int, BaitClaim>();

        /// <summary>Entities we have already considered for the ApproachDistraction task.</summary>
        private static readonly HashSet<int> TaskInjected = new HashSet<int>();

        /// <summary>Hostile entities already taught that a zombie can be an enemy. See EnableZombieVsZombie.</summary>
        private static readonly HashSet<int> TaughtToFightBack = new HashSet<int>();

        /// <summary>
        /// Hostiles currently held on a thrall: entity id -> Time.time the hold expires.
        /// This is what actually keeps aggro, because vanilla's revenge channel cannot - see
        /// EnableZombieVsZombie.
        /// </summary>
        private static readonly Dictionary<int, float> TauntedUntil = new Dictionary<int, float>();

        /// <summary>
        /// Hostiles that have already been asked whether they care about thralls, and said no.
        /// Rolled once each so ThrallTauntShare is a stable split of the horde rather than a
        /// per-second coin flip that pulls everything within a few seconds.
        /// </summary>
        private static readonly HashSet<int> TauntDeclined = new HashSet<int>();

        private static readonly HashSet<string> LoggedOnce = new HashSet<string>();

        /// <summary>Entity class names this mod decided are turnable. Diagnostics and scan.</summary>
        private static readonly List<string> ThrallableNames = new List<string>();

        private static float nextSweep;

        private static void EnsureTags()
        {
            if (tagsReady) return;
            baitItemTag = FastTags<TagGroup.Global>.Parse("chaoticGoreBait");
            thrallTag = FastTags<TagGroup.Global>.Parse("chaoticThrall");
            vanillaZombieTag = FastTags<TagGroup.Global>.Parse("zombie");
            tagsReady = true;
        }

        public static void EnsureTagsPublic() { EnsureTags(); }
        public static FastTags<TagGroup.Global> ThrallTag { get { EnsureTags(); return thrallTag; } }
        public static List<string> Thrallable { get { return ThrallableNames; } }

        // ------------------------------------------------------------------ tagging

        /// <summary>
        /// Decides, from code, which entity classes may be turned - and marks them.
        ///
        /// Done here rather than as an entityclasses.xml xpath for the reason Beastmaster learned
        /// the hard way: an xpath is a single point of failure the mod cannot see fail. Any other
        /// mod that op="set"s Tags after us wipes ours (load order is alphabetical), and a new
        /// zombie tier added by TFP or by a content mod would need the patch rewritten. Here the
        /// rule is applied to whatever classes actually exist at runtime:
        ///
        ///   turnable = name starts with "zombie", is not a template, and is not excluded.
        ///
        /// The name test is deliberate. It admits every walker, every feral/radiated/charged tier,
        /// and any modded zombie that follows the vanilla naming, while keeping out animalZombieDog
        /// and animalZombieBear - those carry the vanilla "zombie" tag too, but they belong to
        /// Chaotic's Beastmaster, and having both mods fight over the same bear is nobody's idea of
        /// a good time. They still eat the gore and still break off a chase; they just never follow
        /// you home.
        ///
        /// Vanilla reads EntityClass.list[...].Tags at the moment it tests the bait
        /// (EntityItem.tickDistraction), not a copy taken at spawn, so setting it here is enough.
        /// </summary>
        public static void ApplyZombieTags()
        {
            EnsureTags();
            ThrallableNames.Clear();

            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddNames(excluded, ZcConfig.ExcludeZombies);

            var forced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddNames(forced, ZcConfig.ExtraThrallable);

            FastTags<TagGroup.Global> one = FastTags<TagGroup.Global>.GetTag("chaoticThrall");

            int noZombieTag = 0;

            foreach (var kv in EntityClass.list.Dict)
            {
                EntityClass ec = kv.Value;
                if (ec == null || string.IsNullOrEmpty(ec.entityClassName)) continue;

                string name = ec.entityClassName;
                bool want = forced.Contains(name);

                if (!want)
                {
                    if (!name.StartsWith("zombie", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("zombieTemplate", StringComparison.OrdinalIgnoreCase)) continue;
                    // Substring, not equality: excluding "zombieScreamer" is meant to take the
                    // feral, radiated and charged screamers with it.
                    if (IsExcluded(name, excluded)) continue;
                    want = true;
                }

                if (!want) continue;

                ec.Tags = ec.Tags | one;
                ThrallableNames.Add(name);

                // The bait's DistractionTags name vanilla's "zombie" tag as well as ours, so a
                // class missing it is still lurable - but it is worth saying, because it usually
                // means another mod rewrote that class's Tags and other things will be off too.
                if (!ec.Tags.Test_AnySet(vanillaZombieTag)) noZombieTag++;
            }

            ThrallableNames.Sort(StringComparer.Ordinal);

            Log.Out("[ZombieCompanion] " + ThrallableNames.Count + " zombie class(es) can be turned"
                + (excluded.Count > 0 ? ", " + excluded.Count + " excluded" : "") + ".");

            if (noZombieTag > 0)
            {
                Log.Warning("[ZombieCompanion] " + noZombieTag + " turnable class(es) do not carry "
                    + "vanilla's 'zombie' tag. They still work, but something in this install is "
                    + "rewriting entity Tags.");
            }

            if (ThrallableNames.Count == 0)
            {
                Log.Warning("[ZombieCompanion] no turnable zombie classes found at all - nothing in "
                    + "this install is named 'zombie*'. The mod will lure but never convert.");
            }
        }

        private static bool IsExcluded(string name, HashSet<string> excluded)
        {
            foreach (string e in excluded)
            {
                if (e.Length > 0 && name.IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static void AddNames(HashSet<string> into, string csv)
        {
            if (string.IsNullOrEmpty(csv)) return;
            string[] parts = csv.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string n = parts[i].Trim();
                if (n.Length > 0) into.Add(n);
            }
        }

        // ------------------------------------------------------------------ plumbing

        public static bool IsServer
        {
            get
            {
                var cm = SingletonMonoBehaviour<ConnectionManager>.Instance;
                return cm != null && cm.IsServer;
            }
        }

        /// <summary>
        /// Runs an action and swallows anything it throws, logging each distinct site once.
        /// These run from AI ticks and Unity events; a throwing handler every frame would both
        /// flood the log and, on a patch prefix, take the game's AI down with it.
        /// </summary>
        public static void Guard(string site, Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                if (LoggedOnce.Add(site))
                {
                    Log.Error("[ZombieCompanion] " + site + " failed (further errors here suppressed): " + e);
                }
            }
        }

        public static void DebugLog(string msg)
        {
            if (ZcConfig.Debug) Log.Out("[ZombieCompanion] " + msg);
        }

        // ------------------------------------------------------------------ queries

        public static bool IsBaitItem(EntityItem item)
        {
            if (item == null || item.itemClass == null) return false;
            EnsureTags();
            return item.itemClass.ItemTags.Test_AnySet(baitItemTag);
        }

        /// <summary>Can be fed to the point of following you.</summary>
        public static bool IsThrallable(EntityAlive e)
        {
            EnsureTags();
            EntityClass ec = ClassOf(e);
            return ec != null && ec.Tags.Test_AnySet(thrallTag);
        }

        /// <summary>
        /// Responds to the gore at all. Superset of IsThrallable: anything vanilla calls a zombie,
        /// which includes the zombie animals and the excluded specials. They eat, you get away,
        /// and that is all they will ever do.
        /// </summary>
        public static bool IsLurable(EntityAlive e)
        {
            EnsureTags();
            EntityClass ec = ClassOf(e);
            if (ec == null) return false;
            return ec.Tags.Test_AnySet(vanillaZombieTag) || ec.Tags.Test_AnySet(thrallTag);
        }

        private static EntityClass ClassOf(EntityAlive e)
        {
            if (e == null) return null;
            EntityClass ec;
            return EntityClass.list.TryGetValue(e.entityClass, out ec) ? ec : null;
        }

        public static bool IsThrall(EntityAlive e)
        {
            return e != null && Thralls.ContainsKey(e.entityId);
        }

        public static bool IsThrall(int entityId)
        {
            return Thralls.ContainsKey(entityId);
        }

        public static EntityPlayer GetOwnerOf(EntityAlive thrall)
        {
            if (thrall == null) return null;
            ThrallState st;
            if (!Thralls.TryGetValue(thrall.entityId, out st)) return null;
            if (st.OwnerEntityId < 0) return null;

            var world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return null;

            var owner = world.GetEntity(st.OwnerEntityId) as EntityPlayer;
            if (owner == null || owner.IsDead()) return null;
            return owner;
        }

        /// <summary>How many of this owner's thralls are standing in the world right now.</summary>
        public static int LiveCountFor(string ownerKey)
        {
            if (string.IsNullOrEmpty(ownerKey)) return 0;
            int n = 0;
            foreach (var kv in Thralls) if (kv.Value.OwnerKey == ownerKey) n++;
            return n;
        }

        /// <summary>Entity ids of this owner's live thralls, oldest binding first.</summary>
        public static List<int> LiveThrallsOf(string ownerKey)
        {
            var ids = new List<int>();
            if (string.IsNullOrEmpty(ownerKey)) return ids;
            foreach (var kv in Thralls) if (kv.Value.OwnerKey == ownerKey) ids.Add(kv.Key);
            ids.Sort((a, b) => Thralls[a].BoundAt.CompareTo(Thralls[b].BoundAt));
            return ids;
        }

        /// <summary>Live thralls of this owner whose class matches, for stow-by-name.</summary>
        public static int FindLiveThrall(string ownerKey, string entityClassName)
        {
            foreach (var kv in Thralls)
            {
                if (kv.Value.OwnerKey == ownerKey && kv.Value.EntityClassName == entityClassName)
                    return kv.Key;
            }
            return -1;
        }

        // ------------------------------------------------------------------ AI task injection

        /// <summary>
        /// Belt and braces. Every vanilla zombie ships ApproachDistraction in its AITask list, which
        /// is why a thrown rock works - so unlike Beastmaster this mod normally has nothing to do
        /// here. It stays because a modded zombie with a hand-written AITask list may well have
        /// dropped it, and the symptom would be "the gore does nothing" with no error anywhere.
        /// </summary>
        public static void EnsureDistractionTask(EAIManager ai)
        {
            if (ai == null) return;
            // Taken off the manager, not off EntityAlive.aiManager: this runs while the manager is
            // still being built, and the entity's back-reference to it may not be set yet.
            EntityAlive e = ai.entity;
            if (e == null) return;
            if (!TaskInjected.Add(e.entityId)) return;
            if (!IsLurable(e)) return;

            var tasks = ai.tasks;
            if (tasks == null) return;
            if (tasks.GetTask<EAIApproachDistraction>() != null) return;

            var task = new EAIApproachDistraction();
            task.Init(e);
            tasks.AddTask(0, task);

            DebugLog("gave ApproachDistraction to " + e.EntityName + " (" + e.entityId + ")");
        }

        /// <summary>
        /// EnsureDistractionTask only ever fires on the one frame an entity is built, so anything
        /// that misses that window is deaf to the bait for the rest of its life with no symptom.
        /// TaskInjected means each entity is only ever considered once, so the steady-state cost is
        /// one hash probe per entity per second.
        /// </summary>
        private static void SweepDistractionTasks(World world)
        {
            var list = world.Entities.list;
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i] as EntityAlive;
                if (a == null || a.IsDead()) continue;
                if (TaskInjected.Contains(a.entityId)) continue;
                if (a.aiManager == null) continue;
                EnsureDistractionTask(a.aiManager);
            }
        }

        // ------------------------------------------------------------------ baiting

        /// <summary>
        /// Called every time a zombie's ApproachDistraction task is asked whether it can run.
        ///
        /// Vanilla's CanExecute bails out while the zombie holds an attack target, and only keeps
        /// the pending distraction rather than acting on it. That is precisely the case this mod
        /// exists for - gore thrown at something already on you. So if the bait is ours and the
        /// zombie is hunting a player, drop the target here, before vanilla looks at it, and hold a
        /// short window where it may not re-acquire.
        ///
        /// The window only has to cover the gap until EAIApproachDistraction.Start() sets
        /// theEntity.distraction, because from that moment vanilla suppresses re-targeting by itself
        /// (EAISetNearestEntityAsTarget.CanExecute returns false while distraction != null).
        /// </summary>
        public static void OnDistractionConsidered(EntityAlive zombie)
        {
            if (zombie == null || !IsServer) return;

            EntityItem bait = zombie.pendingDistraction;
            if (!IsBaitItem(bait)) return;
            if (!IsLurable(zombie))
            {
                DebugLog(zombie.EntityName + " (" + zombie.entityId + ") saw gore but is not a zombie");
                return;
            }

            DebugLog(zombie.EntityName + " (" + zombie.entityId + ") considering gore " + bait.entityId
                + " target=" + (zombie.GetAttackTarget() != null ? zombie.GetAttackTarget().EntityName : "none"));

            // One pile feeds one zombie. Vanilla pulses a decoy at every eligible entity in radius,
            // so without this a whole horde peels onto a single pile - and since a feed is scored
            // per BAIT, only the first one to finish would get credit anyway. Losing the claim means
            // this one keeps coming for you, which is the honest outcome.
            if (!TryClaimBait(zombie, bait))
            {
                zombie.pendingDistraction = null;
                return;
            }

            BaitState st = GetOrCreateBaitState(zombie.entityId);

            // belongsPlayerId is set to the thrower's entity id by GameManager.ItemDropServer,
            // which is what ItemActionThrowAway calls. That is our feeder.
            if (bait.belongsPlayerId >= 0) st.FeederEntityId = bait.belongsPlayerId;

            if (!ZcConfig.BreakActiveAggro) return;

            var target = zombie.GetAttackTarget();
            if (target is EntityPlayer)
            {
                st.PacifyUntil = Time.time + ZcConfig.PacifySeconds;
                zombie.SetAttackTarget(null, 0);
                DebugLog(zombie.EntityName + " (" + zombie.entityId + ") broke off its chase for gore");
            }
        }

        /// <summary>
        /// Decides whether this zombie is allowed to work on this particular bait.
        ///
        /// Claims are per bait item, capped at ZcConfig.MaxZombiesPerBait (1 by default). A closer
        /// zombie can take the claim off a further one, but only while that one is still walking
        /// over - once it has actually committed (distraction == bait) it keeps the meal. Without
        /// the distance rule the winner would just be whichever AI ticked first, which would
        /// routinely hand the gore to something 25m away while the one on top of you kept swinging.
        /// </summary>
        private static bool TryClaimBait(EntityAlive zombie, EntityItem bait)
        {
            int max = ZcConfig.MaxZombiesPerBait;
            if (max <= 0) return true; // 0 = unlimited, vanilla decoy behaviour

            var world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return true;

            BaitClaim claim;
            if (!BaitClaims.TryGetValue(bait.entityId, out claim))
            {
                claim = new BaitClaim();
                BaitClaims[bait.entityId] = claim;
            }

            float now = Time.time;

            // Drop claimants that died, despawned, or wandered off this bait. The grace period
            // covers the frame gap between us granting a claim and the AI task actually starting.
            List<int> gone = null;
            foreach (var kv in claim.Zombies)
            {
                if (kv.Key == zombie.entityId) continue;
                var a = world.GetEntity(kv.Key) as EntityAlive;
                bool stillOnIt = a != null && !a.IsDead()
                                 && (a.distraction == bait || a.pendingDistraction == bait);
                if (!stillOnIt && now - kv.Value > 3f) (gone ?? (gone = new List<int>())).Add(kv.Key);
            }
            if (gone != null) for (int i = 0; i < gone.Count; i++) claim.Zombies.Remove(gone[i]);

            if (claim.Zombies.ContainsKey(zombie.entityId))
            {
                claim.Zombies[zombie.entityId] = now;
                return true;
            }

            if (claim.Zombies.Count < max)
            {
                claim.Zombies[zombie.entityId] = now;
                return true;
            }

            // Full. Take it off the furthest incumbent if we are closer and it has not started
            // eating yet.
            float myDistSq = zombie.GetDistanceSq(bait);
            int evict = -1;
            float worstDistSq = myDistSq;

            foreach (var kv in claim.Zombies)
            {
                var a = world.GetEntity(kv.Key) as EntityAlive;
                if (a == null || a.IsDead()) { evict = kv.Key; break; }
                if (a.distraction == bait) continue; // committed, leave it alone
                float d = a.GetDistanceSq(bait);
                if (d > worstDistSq) { worstDistSq = d; evict = kv.Key; }
            }

            if (evict < 0) return false;

            var loser = world.GetEntity(evict) as EntityAlive;
            if (loser != null && loser.pendingDistraction == bait) loser.pendingDistraction = null;
            claim.Zombies.Remove(evict);
            claim.Zombies[zombie.entityId] = now;

            DebugLog(zombie.EntityName + " (" + zombie.entityId + ") took gore " + bait.entityId
                + " from " + evict + " (closer)");
            return true;
        }

        private static BaitState GetOrCreateBaitState(int entityId)
        {
            BaitState st;
            if (!Baited.TryGetValue(entityId, out st))
            {
                st = new BaitState();
                Baited[entityId] = st;
            }
            return st;
        }

        /// <summary>
        /// Gate on EntityAlive.SetAttackTarget. Returns false to drop the call.
        /// Two jobs: hold the pacify window open while a baited zombie disengages, and make a thrall
        /// structurally incapable of turning on a player.
        /// </summary>
        public static bool AllowAttackTarget(EntityAlive self, EntityAlive target)
        {
            // Clearing a target must always work - vanilla's own AI does this constantly, and
            // EAIApproachDistraction.Start() depends on it.
            if (self == null || target == null) return true;
            if (!IsServer) return true;

            ThrallState st;
            if (Thralls.TryGetValue(self.entityId, out st))
            {
                if (!ZcConfig.ThrallsAttackPlayers && target is EntityPlayer) return false;

                // Never the owner, whatever the config says. Friendly fire from the person holding
                // the leash is the single most likely way a thrall would otherwise turn.
                if (target.entityId == st.OwnerEntityId) return false;

                // Never another thrall. Without this, two players hunting together would watch
                // their companions immediately go for each other - both are EntityZombie, and
                // EntityZombie is exactly what a thrall is told to hunt.
                if (Thralls.ContainsKey(target.entityId)) return false;
                return true;
            }

            BaitState bs;
            if (Baited.TryGetValue(self.entityId, out bs) && Time.time < bs.PacifyUntil && target is EntityPlayer)
            {
                return false;
            }

            // Holding a taunt. A zombie fighting a thrall must not be allowed to drift back onto
            // the player, and this is the only place that can stop it: EAISetNearestEntityAsTarget
            // re-scans on its own schedule and calls SetAttackTarget(player) with no regard for
            // what the entity is already busy with.
            float until;
            if (TauntedUntil.TryGetValue(self.entityId, out until) && target is EntityPlayer)
            {
                if (Time.time < until) return false;
                TauntedUntil.Remove(self.entityId);
            }

            return true;
        }

        /// <summary>
        /// Called from the tail of EAIApproachDistraction.Update, which is where vanilla decrements
        /// distractionEatTicks while the zombie has its head down. When that counter runs out the
        /// meal is finished (EntityItem.OnUpdateEntity kills the item on the same condition), so
        /// that is the moment a feed is scored - not when the zombie merely arrives.
        /// </summary>
        public static void OnEatTick(EntityAlive zombie, EntityItem bait)
        {
            if (zombie == null || bait == null || !IsServer) return;
            if (!IsBaitItem(bait)) return;
            if (bait.distractionEatTicks > 0) return;

            // One pile, one feed, no matter how many ticks land on this frame.
            if (!ConsumedBait.Add(bait.entityId)) return;

            if (Thralls.ContainsKey(zombie.entityId))
            {
                HealThrall(zombie, bait);
                return;
            }

            // Lurable but not turnable - the zombie animals, the demolisher, the screamer. It ate,
            // it was busy, you got away, and that is all it will ever do. Say so, otherwise the
            // player sees a counter that never moves and assumes the mod is broken.
            if (!IsThrallable(zombie))
            {
                BaitState never = GetOrCreateBaitState(zombie.entityId);
                if (bait.belongsPlayerId >= 0) never.FeederEntityId = bait.belongsPlayerId;
                var whoFed = FeederOf(never);
                if (ZcConfig.NotifyProgress && whoFed != null)
                {
                    GameManager.ShowTooltipMP(whoFed,
                        FriendlyName(zombie) + " buries its face in it. That one will never take the leash - move.",
                        null);
                }
                return;
            }

            BaitState st = GetOrCreateBaitState(zombie.entityId);
            st.Feeds += FeedValueOf(bait);

            var feeder = FeederOf(st);
            int needed = FeedsNeededFor(zombie);

            if (st.Feeds >= needed)
            {
                Bind(zombie, feeder, st);
                return;
            }

            if (ZcConfig.NotifyProgress && feeder != null)
            {
                GameManager.ShowTooltipMP(feeder,
                    FriendlyName(zombie) + " feeds, and stops looking at you like food. ("
                    + st.Feeds + "/" + needed + ")", null);
            }

            DebugLog(FriendlyName(zombie) + " (" + zombie.entityId + ") fed " + st.Feeds + "/" + needed);
        }

        /// <summary>
        /// Meals this particular zombie needs. The tiers cost more, because otherwise nobody would
        /// ever turn a walker. Radiated carries the feral tag as well and charged carries both, so
        /// the test runs hardest-first and only the highest bonus applies.
        /// </summary>
        public static int FeedsNeededFor(EntityAlive zombie)
        {
            int n = ZcConfig.FeedsToThrall;
            EntityClass ec = ClassOf(zombie);
            if (ec == null) return n;

            if (HasAnyTag(ec, "charged") || HasAnyTag(ec, "infernal")) return n + ZcConfig.ChargedFeedBonus;
            if (HasAnyTag(ec, "radiated")) return n + ZcConfig.RadiatedFeedBonus;
            if (HasAnyTag(ec, "feral")) return n + ZcConfig.FeralFeedBonus;
            return n;
        }

        private static bool HasAnyTag(EntityClass ec, string tag)
        {
            return ec.Tags.Test_AnySet(FastTags<TagGroup.Global>.GetTag(tag));
        }

        private static int FeedValueOf(EntityItem bait)
        {
            if (bait.itemClass == null) return 1;
            return bait.itemClass.Name == "chaoticGoreBaitRipe" ? ZcConfig.RipeBaitFeedValue : 1;
        }

        private static EntityPlayer FeederOf(BaitState st)
        {
            if (st == null || st.FeederEntityId < 0) return null;
            var world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return null;
            return world.GetEntity(st.FeederEntityId) as EntityPlayer;
        }

        /// <summary>Gore fed to a zombie that is already yours patches it up instead.</summary>
        private static void HealThrall(EntityAlive thrall, EntityItem bait)
        {
            int max = thrall.GetMaxHealth();
            bool ripe = FeedValueOf(bait) >= ZcConfig.RipeBaitFeedValue;
            int heal = Mathf.Max(1, Mathf.RoundToInt(max * (ripe ? 0.5f : 0.25f)));
            // AddHealth clamps to max internally, so no need to work out the shortfall first.
            thrall.AddHealth(heal);

            // Feeding also renews the leash, so a server running DecayMinutes can keep a thrall
            // alive indefinitely at the cost of a steady gore supply.
            ThrallState st;
            if (Thralls.TryGetValue(thrall.entityId, out st)) st.BoundAt = Time.time;

            var owner = GetOwnerOf(thrall);
            if (owner != null && ZcConfig.NotifyProgress)
            {
                GameManager.ShowTooltipMP(owner,
                    FriendlyName(thrall) + " tears it apart. (" + thrall.Health + "/" + max + " HP)", null);
            }
        }

        // ------------------------------------------------------------------ binding

        private static void Bind(EntityAlive zombie, EntityPlayer owner, BaitState st)
        {
            Baited.Remove(zombie.entityId);

            if (owner == null)
            {
                // Fed to the threshold by someone who has since died or logged off. Drop it rather
                // than bind it to nobody, otherwise the zombie is stuck in limbo.
                DebugLog("bind threshold reached but the feeder is gone; resetting " + zombie.entityId);
                return;
            }

            string ownerKey = OwnerKeyOf(owner);
            if (string.IsNullOrEmpty(ownerKey))
            {
                DebugLog("no stable owner key for " + owner.EntityName + "; binding without persistence");
            }

            // The roster is what limits you, not the field. Full means you have to let one go
            // before another will follow you - the zombie stays hostile and its progress resets.
            if (!string.IsNullOrEmpty(ownerKey) && ThrallStore.Count(ownerKey) >= ZcConfig.MaxOwnedThralls)
            {
                GameManager.ShowTooltipMP(owner,
                    FriendlyName(zombie) + " will not take the leash - you are already holding "
                    + ZcConfig.MaxOwnedThralls + ". Use /thrall release <n> first.", null);
                return;
            }

            // Only so many out at a time: the oldest one steps back so the new one can take its
            // place. Measured on the STORE rather than on what is standing in the world - a thrall
            // that is marked out but has not respawned yet still holds its slot, and counting live
            // bodies instead would quietly hand out a slot that ThrallStore.Add then refuses.
            string stowed = null;
            if (!string.IsNullOrEmpty(ownerKey) && ThrallStore.ActiveCount(ownerKey) >= ZcConfig.MaxActiveThralls)
            {
                var live = LiveThrallsOf(ownerKey);
                if (live.Count > 0)
                {
                    ThrallState oldest = Thralls[live[0]];
                    stowed = PrettyClassName(oldest.EntityClassName);
                    ThrallStore.SetActiveByClass(ownerKey, oldest.EntityClassName, false);
                    DespawnThrall(live[0]);
                }
                else
                {
                    // Marked out, nothing on its feet - a queued respawn that has not landed. Stand
                    // the oldest record down so the new one has a slot to take.
                    var actives = ThrallStore.Actives(ownerKey);
                    if (actives.Count > 0)
                    {
                        stowed = PrettyClassName(actives[0].EntityClassName);
                        ThrallStore.SetActiveByClass(ownerKey, actives[0].EntityClassName, false);
                    }
                }
            }

            if (!string.IsNullOrEmpty(ownerKey)
                && !ThrallStore.Add(ownerKey, EntityClassNameOf(zombie), true))
            {
                GameManager.ShowTooltipMP(owner, FriendlyName(zombie) + " will not take the leash - roster full.", null);
                return;
            }

            MakeThrall(zombie, owner, ownerKey);

            GameManager.ShowTooltipMP(owner,
                FriendlyName(zombie) + " stops chewing, straightens up, and waits for you. It is yours now."
                + (stowed != null ? "\nYour " + stowed + " shuffles off to wait - /thrall list" : ""), null);

            Log.Out("[ZombieCompanion] " + owner.EntityName + " bound a " + EntityClassNameOf(zombie)
                + " (entity " + zombie.entityId + ")");
        }

        /// <summary>
        /// Takes a thrall out of the world without killing it. Used by /thrall stow, by the roster
        /// cap, and on logout.
        ///
        /// Despawned, not Killed: EnumRemoveEntityReason.Killed fires the EntityKilled event, and
        /// that handler deletes the saved record - so stowing would permanently destroy the thrall.
        /// </summary>
        public static void DespawnThrall(int entityId)
        {
            var world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return;
            if (!Thralls.Remove(entityId)) return;

            world.RemoveEntity(entityId, EnumRemoveEntityReason.Despawned);
            DebugLog("stowed thrall " + entityId);
        }

        /// <summary>Takes every one of this owner's thralls off the field. Logout and shutdown.</summary>
        public static void DespawnAllOf(string ownerKey)
        {
            var live = LiveThrallsOf(ownerKey);
            for (int i = 0; i < live.Count; i++) DespawnThrall(live[i]);
        }

        /// <summary>
        /// Turns a hostile zombie into a thrall in place - no despawn/respawn, so the same thing you
        /// fed is the one that follows you.
        ///
        /// The rewiring is all done on vanilla AI objects rather than by adding custom combat AI:
        /// the zombie already knows how to chase and swing, it just has the wrong list of things it
        /// is willing to chase and swing at. Swapping those lists is both less code and better
        /// behaved than a bespoke attack task.
        /// </summary>
        public static void MakeThrall(EntityAlive zombie, EntityPlayer owner, string ownerKey)
        {
            var st = new ThrallState
            {
                ThrallEntityId = zombie.entityId,
                OwnerEntityId = owner != null ? owner.entityId : -1,
                OwnerKey = ownerKey,
                EntityClassName = EntityClassNameOf(zombie),
                BoundAt = Time.time
            };
            Thralls[zombie.entityId] = st;

            zombie.SetAttackTarget(null, 0);
            zombie.SetRevengeTarget(null);

            // A thrall must not be culled by the wandering-horde/biome budget, and must not be
            // counted against it either - it is the player's, not the spawner's.
            zombie.SetSpawnerSource(EnumSpawnerSource.StaticSpawner);

            // A sleeper keeps a spawn position and a 90s forced "go home" timer, so a thrall turned
            // inside a POI would abandon a fight halfway through to walk back to its closet.
            zombie.IsSleeper = false;
            zombie.IsSleeperPassive = false;
            zombie.ChaseReturnLocation = Vector3.zero;

            var ai = zombie.aiManager;
            if (ai == null) return;

            // What it will chase. EntityEnemy is the common base of every zombie, every bandit and
            // the zombie animals, so one entry covers the lot - and nothing else in the game
            // inherits from it, which is what keeps players and livestock off the list.
            var attack = ai.tasks != null ? ai.tasks.GetTask<EAIApproachAndAttackTarget>() : null;
            if (attack != null)
            {
                attack.targetClasses = new List<EAIApproachAndAttackTarget.TargetClass>
                {
                    NewChaseClass(typeof(EntityEnemy), 60f),
                };
            }

            // What it goes looking for. playerTargetClassIndex must be reset too - it is a cached
            // index into the list we just replaced, and FindTarget uses it to decide whether to run
            // the player-specific sight/noise/smell path at all.
            var acquire = ai.targetTasks != null ? ai.targetTasks.GetTask<EAISetNearestEntityAsTarget>() : null;
            if (acquire != null)
            {
                acquire.targetClasses = new List<EAISetNearestEntityAsTarget.TargetClass>
                {
                    NewSeekClass(typeof(EntityEnemy), 30f, 25f),
                };
                acquire.playerTargetClassIndex = -1;
            }

            // Fight back when hurt, but only against other hostiles - the vanilla list is players,
            // bandits and enemy animals, and a thrall that retaliated against a player would undo
            // the whole point.
            var ifHurt = ai.targetTasks != null ? ai.targetTasks.GetTask<EAISetAsTargetIfHurt>() : null;
            if (ifHurt != null)
            {
                ifHurt.targetClasses = new List<EAISetAsTargetIfHurt.TargetClass>
                {
                    new EAISetAsTargetIfHurt.TargetClass { type = typeof(EntityEnemy) },
                };
            }

            // The tasks that make a zombie a zombie rather than a companion:
            //   BreakBlock/DestroyArea - it follows you home and eats your walls.
            //   Territorial/Wander/ApproachSpot - it wanders off.
            //   SetNearestCorpseAsTarget - it stops to eat every body on the way.
            if (!ZcConfig.ThrallsBreakBlocks)
            {
                RemoveTask<EAIBreakBlock>(ai.tasks);
                RemoveTask<EAIDestroyArea>(ai.tasks);
            }
            RemoveTask<EAITerritorial>(ai.tasks);
            RemoveTask<EAIWander>(ai.tasks);
            RemoveTask<EAIApproachSpot>(ai.tasks);
            RemoveTask<EAIRunawayWhenHurt>(ai.tasks);
            RemoveTargetTask<EAISetNearestCorpseAsTarget>(ai.targetTasks);

            // Priority 5 sits below ApproachAndAttackTarget (4 on a vanilla zombie), so fighting
            // always beats heeling, and above nothing else now that Wander is gone.
            if (ai.tasks != null && ai.tasks.GetTask<EAIFollowMaster>() == null)
            {
                var follow = new EAIFollowMaster();
                follow.Init(zombie);
                ai.tasks.AddTask(5, follow);
            }

            // Stat.Max is read-only and derived; BaseMax is the settable input GetMaxHealth()
            // ultimately reads through Stats.Health.Max.
            if (ZcConfig.ThrallHealthScale != 1f && zombie.Stats != null && zombie.Stats.Health != null)
            {
                zombie.Stats.Health.BaseMax = zombie.Stats.Health.BaseMax * ZcConfig.ThrallHealthScale;
            }

            // Binding heals it up: it just ate its way through several piles of gore, and a thrall
            // that arrives on 12 HP because you shot it before you thought better of it is a thrall
            // that dies to the first walker.
            int missing = zombie.GetMaxHealth() - zombie.Health;
            if (missing > 0) zombie.AddHealth(missing);

            if (owner != null)
            {
                zombie.SetEntityName(owner.EntityName + "'s " + FriendlyName(zombie));
            }
        }

        /// <summary>
        /// Why zombies cannot fight zombies in vanilla, and what this does about it.
        ///
        /// There are TWO separate blocks in the game, and clearing one without the other gets you
        /// nothing:
        ///
        ///   1. EAIApproachAndAttackTarget.CanExecute walks its own targetClasses list and returns
        ///      false for any target whose Type is not in it. A zombie's list is EntityPlayer,
        ///      EntityBandit, EntityEnemyAnimal and EntityAnimal - no zombie. So even a zombie
        ///      holding a thrall as its attack target will not take one step towards it.
        ///
        ///   2. The revenge channel - the normal "this is what hurt me" path - is hard-blocked
        ///      between things of the same kind. EAISetAsTargetIfHurt.CanExecute opens with
        ///      `revengeTarget.entityType != theEntity.entityType`, and EntityType has exactly one
        ///      value for every zombie in the game (Unknown/Player/Zombie/Animal/Bandit). Two
        ///      zombies are always the same entityType, so SetRevengeTarget between them is inert.
        ///      No amount of AI-list editing fixes that one; it is a type check, not a list.
        ///
        /// So this method clears block 1 - append EntityEnemy to the chase list, which covers every
        /// zombie, bandit and enemy animal in one entry and structurally cannot catch a player -
        /// and Taunt() works around block 2 by holding the target itself. Applied lazily, per
        /// entity, so it costs nothing until a thrall is actually in a fight.
        ///
        /// The if-hurt list gets EntityEnemy too. That is NOT dead code: a bandit or an enemy animal
        /// is a different entityType from a zombie thrall, so revenge works normally for those.
        /// </summary>
        private static void EnableZombieVsZombie(EntityAlive victim)
        {
            if (!TaughtToFightBack.Add(victim.entityId)) return;

            var ai = victim.aiManager;
            if (ai == null) return;

            var attack = ai.tasks != null ? ai.tasks.GetTask<EAIApproachAndAttackTarget>() : null;
            if (attack != null && attack.targetClasses != null)
            {
                attack.targetClasses.Add(NewChaseClass(typeof(EntityEnemy), 30f));
            }

            var ifHurt = ai.targetTasks != null ? ai.targetTasks.GetTask<EAISetAsTargetIfHurt>() : null;
            if (ifHurt != null && ifHurt.targetClasses != null)
            {
                ifHurt.targetClasses.Add(new EAISetAsTargetIfHurt.TargetClass { type = typeof(EntityEnemy) });
            }

            DebugLog("taught " + victim.EntityName + " (" + victim.entityId + ") to fight thralls");
        }

        /// <summary>
        /// Puts a hostile onto a thrall and HOLDS it there.
        ///
        /// The hold is the whole point. Setting an attack target on its own lasts until
        /// EAISetNearestEntityAsTarget next runs - it re-scans on its own schedule, finds the
        /// player, and calls SetAttackTarget(player) with no regard for what the entity was busy
        /// with. Since the revenge channel that would normally outrank that is blocked between
        /// zombies (see EnableZombieVsZombie), the hold is enforced in AllowAttackTarget instead:
        /// while the window is open, this entity simply may not be given a player as a target.
        /// </summary>
        public static void Taunt(EntityAlive victim, EntityAlive thrall)
        {
            if (victim == null || thrall == null) return;
            if (!ZcConfig.ThrallsDrawAggro) return;
            if (Thralls.ContainsKey(victim.entityId)) return;
            if (!(victim is EntityEnemy)) return;

            EnableZombieVsZombie(victim);

            TauntedUntil[victim.entityId] = Time.time + ZcConfig.TauntSeconds;
            victim.SetRevengeTarget(thrall);   // works for bandits and enemy animals
            victim.SetAttackTarget(thrall, 400);
        }

        /// <summary>
        /// Once a second: hand part of the horde standing near a thrall over to it.
        ///
        /// Retaliation alone is not enough to make a thrall read as a companion - it means your
        /// thrall picks a fight with one zombie while the other nine walk straight past it to you.
        /// This is what turns a thrall into something you can stand behind.
        ///
        /// Deliberately partial. ThrallTauntShare is rolled ONCE per zombie and the refusals are
        /// remembered, so half the horde commits to the thrall and the other half is still your
        /// problem. Rolling every second instead would creep to 100% within a few seconds and turn
        /// a blood moon into a spectator sport.
        ///
        /// Only takes zombies that are idle or already coming for this thrall's owner: something
        /// mid-fight with another player is left alone.
        /// </summary>
        private static void PullAggroToThralls(World world)
        {
            if (!ZcConfig.ThrallsDrawAggro) return;
            if (Thralls.Count == 0) return;
            if (ZcConfig.ThrallTauntRadius <= 0f || ZcConfig.ThrallTauntShare <= 0f) return;

            float radiusSq = ZcConfig.ThrallTauntRadius * ZcConfig.ThrallTauntRadius;
            var list = world.Entities.list;

            for (int i = 0; i < list.Count; i++)
            {
                var z = list[i] as EntityEnemy;
                if (z == null || z.IsDead() || z.IsSleeping || z.sleepingOrWakingUp) continue;
                if (Thralls.ContainsKey(z.entityId)) continue;
                if (TauntDeclined.Contains(z.entityId)) continue;

                // Nearest thrall in range, so a zombie between two thralls picks the closer.
                EntityAlive best = null;
                ThrallState bestState = null;
                float bestSq = radiusSq;
                foreach (var kv in Thralls)
                {
                    var t = world.GetEntity(kv.Key) as EntityAlive;
                    if (t == null || t.IsDead()) continue;
                    float d = z.GetDistanceSq(t);
                    if (d > bestSq) continue;
                    bestSq = d;
                    best = t;
                    bestState = kv.Value;
                }
                if (best == null) continue;

                EntityAlive current = z.GetAttackTarget();
                if (current == best)
                {
                    // Already ours - just keep the window open so it does not drift back.
                    TauntedUntil[z.entityId] = Time.time + ZcConfig.TauntSeconds;
                    continue;
                }

                // Free to take, or it is coming for this thrall's owner. Anything busy with someone
                // else stays busy with them.
                bool takeable = current == null
                    || (current is EntityPlayer && current.entityId == bestState.OwnerEntityId);
                if (!takeable) continue;

                if (UnityEngine.Random.value > ZcConfig.ThrallTauntShare)
                {
                    TauntDeclined.Add(z.entityId);
                    continue;
                }

                Taunt(z, best);
                DebugLog(z.EntityName + " (" + z.entityId + ") pulled onto thrall " + best.entityId);
            }
        }

        private static EAIApproachAndAttackTarget.TargetClass NewChaseClass(Type t, float chaseTimeMax)
        {
            return new EAIApproachAndAttackTarget.TargetClass { type = t, chaseTimeMax = chaseTimeMax };
        }

        private static EAISetNearestEntityAsTarget.TargetClass NewSeekClass(Type t, float hear, float see)
        {
            return new EAISetNearestEntityAsTarget.TargetClass { type = t, hearDistMax = hear, seeDistMax = see };
        }

        private static void RemoveTask<T>(EAITaskList list) where T : class
        {
            if (list == null) return;
            for (int i = list.Tasks.Count - 1; i >= 0; i--)
            {
                if (list.Tasks[i].action is T) list.RemoveTask(list.Tasks[i]);
            }
        }

        private static void RemoveTargetTask<T>(EAITaskList list) where T : class
        {
            RemoveTask<T>(list);
        }

        // ------------------------------------------------------------------ naming / ids

        public static string EntityClassNameOf(EntityAlive e)
        {
            EntityClass ec = ClassOf(e);
            return ec != null ? ec.entityClassName : "zombieArlene";
        }

        /// <summary>"zombieBusinessManFeral" -> "Business Man Feral", for tooltips.</summary>
        public static string FriendlyName(EntityAlive e)
        {
            return PrettyClassName(EntityClassNameOf(e));
        }

        /// <summary>Same, for a class name we only have as a string (saved records).</summary>
        public static string PrettyClassName(string className)
        {
            string n = className ?? "";
            if (n.StartsWith("zombie", StringComparison.OrdinalIgnoreCase)) n = n.Substring(6);
            else if (n.StartsWith("animal", StringComparison.OrdinalIgnoreCase)) n = n.Substring(6);
            if (n.Length == 0) return "Thrall";

            var sb = new System.Text.StringBuilder(n.Length + 4);
            for (int i = 0; i < n.Length; i++)
            {
                if (i > 0 && char.IsUpper(n[i]) && !char.IsUpper(n[i - 1])) sb.Append(' ');
                sb.Append(i == 0 ? char.ToUpperInvariant(n[i]) : n[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Stable across sessions and across entity-id churn, unlike EntityPlayer.entityId.
        ///
        /// Tried in order, because no single source covers every kind of game:
        ///   1. The ClientInfo, which is what a dedicated server always has.
        ///   2. The persistent player list. The HOST of a client-hosted game has no ClientInfo at
        ///      all - they are not a network client of their own server - so ForEntityId returns
        ///      null and step 1 gives up. Skipping this step is what made /pet dead for every
        ///      non-dedicated game in Beastmaster 1.2.2.
        ///   3. persistentLocalPlayer, for the moment early in a host's session before their entity
        ///      id has been mapped into the list.
        /// Null only if all three fail, which means binding still works but will not persist.
        /// </summary>
        public static string OwnerKeyOf(EntityPlayer player)
        {
            if (player == null) return null;

            var cm = SingletonMonoBehaviour<ConnectionManager>.Instance;
            if (cm != null && cm.Clients != null)
            {
                ClientInfo ci = cm.Clients.ForEntityId(player.entityId);
                if (ci != null && ci.PlatformId != null) return ci.PlatformId.CombinedString;
            }

            var gm = GameManager.Instance;
            if (gm == null) return null;

            if (gm.persistentPlayers != null)
            {
                PersistentPlayerData ppd = gm.persistentPlayers.GetPlayerDataFromEntityID(player.entityId);
                if (ppd != null && ppd.PrimaryId != null) return ppd.PrimaryId.CombinedString;
            }

            if (player is EntityPlayerLocal && gm.persistentLocalPlayer != null
                && gm.persistentLocalPlayer.PrimaryId != null)
            {
                return gm.persistentLocalPlayer.PrimaryId.CombinedString;
            }

            return null;
        }

        // ------------------------------------------------------------------ lifecycle events

        public static void OnPlayerSpawned(int entityId, RespawnType respawnType)
        {
            if (!IsServer) return;
            ThrallRespawner.OnPlayerSpawned(entityId, respawnType);
        }

        public static void OnPlayerLeft(ClientInfo ci)
        {
            if (!IsServer || ci == null) return;
            ThrallRespawner.OnPlayerLeft(ci);
        }

        public static void OnEntityKilled(Entity killed, Entity killer)
        {
            if (!IsServer || killed == null) return;

            ThrallState st;
            if (Thralls.TryGetValue(killed.entityId, out st))
            {
                Thralls.Remove(killed.entityId);
                ThrallStore.Forget(st.OwnerKey, st.EntityClassName);

                var world = GameManager.Instance != null ? GameManager.Instance.World : null;
                var owner = world != null ? world.GetEntity(st.OwnerEntityId) as EntityPlayer : null;
                if (owner != null)
                {
                    GameManager.ShowTooltipMP(owner,
                        PrettyClassName(st.EntityClassName) + " comes apart. It stayed between you and them.", null);
                }
                Log.Out("[ZombieCompanion] thrall " + st.EntityClassName + " (" + killed.entityId + ") died");
            }

            Baited.Remove(killed.entityId);
            TaskInjected.Remove(killed.entityId);
            TaughtToFightBack.Remove(killed.entityId);
            TauntedUntil.Remove(killed.entityId);
            TauntDeclined.Remove(killed.entityId);
        }

        /// <summary>
        /// Every thrall comes off the field before the world is written out.
        ///
        /// This is not tidiness. A thrall is spawned as StaticSpawner so the horde manager cannot
        /// cull it, and EntityEnemy.IsSavedToFile only returns false for Dynamic - so left alone a
        /// thrall would be written into the chunk, and would load on the next restart as an
        /// ordinary hostile zombie standing in your base wearing your name. The IsSavedToFile patch
        /// is the real defence; this is the belt to its braces.
        /// </summary>
        public static void OnShutdown()
        {
            var ids = new List<int>(Thralls.Keys);
            for (int i = 0; i < ids.Count; i++) DespawnThrall(ids[i]);

            ThrallStore.Flush();
            Baited.Clear();
            Thralls.Clear();
            ConsumedBait.Clear();
            TaskInjected.Clear();
            TaughtToFightBack.Clear();
            TauntedUntil.Clear();
            TauntDeclined.Clear();
        }

        /// <summary>
        /// Once a second: drop bookkeeping for entities that no longer exist, re-point live thralls
        /// at their owner's current entity id (which changes on every respawn and relog), expire
        /// leashes, and put back anything that vanished with its chunk. Without the sweep these
        /// dictionaries would grow for the life of the server, because an entity that despawns on
        /// chunk unload never raises EntityKilled.
        /// </summary>
        public static void Tick()
        {
            if (!IsServer) return;
            if (Time.time < nextSweep) return;
            nextSweep = Time.time + 1f;

            var world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return;

            ThrallRespawner.Tick(world);
            SweepDistractionTasks(world);
            PullAggroToThralls(world);
            ExpireLeashes();
            if (ZcConfig.Debug) TraceLiveBait(world);

            PruneMissing(world, Baited);
            PruneMissing(world, Thralls);

            if (ConsumedBait.Count > 512) ConsumedBait.Clear();

            // Claims outlive their bait once it is eaten or despawns.
            if (BaitClaims.Count > 0)
            {
                List<int> deadBait = null;
                foreach (var kv in BaitClaims)
                {
                    Entity b = world.GetEntity(kv.Key);
                    if (b == null || b.IsDead()) (deadBait ?? (deadBait = new List<int>())).Add(kv.Key);
                }
                if (deadBait != null) for (int i = 0; i < deadBait.Count; i++) BaitClaims.Remove(deadBait[i]);
            }

            PruneStaleIds(world, TaskInjected, 2048);
            PruneStaleIds(world, TaughtToFightBack, 512);
            PruneStaleIds(world, TauntDeclined, 512);

            // Expired holds, dropped eagerly: this map is read on every SetAttackTarget call in the
            // game, so it must not be allowed to accumulate a horde's worth of dead ids.
            if (TauntedUntil.Count > 0)
            {
                List<int> done = null;
                foreach (var kv in TauntedUntil)
                {
                    if (Time.time >= kv.Value || world.GetEntity(kv.Key) == null)
                        (done ?? (done = new List<int>())).Add(kv.Key);
                }
                if (done != null) for (int i = 0; i < done.Count; i++) TauntedUntil.Remove(done[i]);
            }
        }

        /// <summary>
        /// DecayMinutes: the gore wears off and the thrall wanders away for good. Off by default -
        /// this is here for servers that want thralls to be a consumable rather than a pet.
        /// Feeding one more pile of gore resets the clock (see HealThrall).
        /// </summary>
        private static void ExpireLeashes()
        {
            if (ZcConfig.DecayMinutes <= 0f) return;

            float cutoff = ZcConfig.DecayMinutes * 60f;
            List<int> expired = null;
            foreach (var kv in Thralls)
            {
                if (Time.time - kv.Value.BoundAt > cutoff) (expired ?? (expired = new List<int>())).Add(kv.Key);
            }
            if (expired == null) return;

            for (int i = 0; i < expired.Count; i++)
            {
                ThrallState st = Thralls[expired[i]];
                var owner = GetOwnerOf(GameManager.Instance.World.GetEntity(expired[i]) as EntityAlive);
                ThrallStore.Forget(st.OwnerKey, st.EntityClassName);
                DespawnThrall(expired[i]);
                if (owner != null && ZcConfig.NotifyProgress)
                {
                    GameManager.ShowTooltipMP(owner,
                        PrettyClassName(st.EntityClassName) + " loses interest in you and shambles off.", null);
                }
                Log.Out("[ZombieCompanion] leash expired on " + st.EntityClassName + " (" + expired[i] + ")");
            }
        }

        /// <summary>
        /// One line a second, per bait lying in the world, pairing it with the nearest lurable
        /// zombie. Every link in the chain is on that line, so whichever one is broken is visible
        /// without a debugger: the bait's own state (does it still live, has it landed, does it
        /// still pulse), and the zombie's (does it carry the AI task, has the pulse reached it, is
        /// it still locked on the player).
        /// </summary>
        private static void TraceLiveBait(World world)
        {
            var entities = world.Entities.list;
            for (int i = 0; i < entities.Count; i++)
            {
                var bait = entities[i] as EntityItem;
                if (bait == null || bait.IsDead() || !IsBaitItem(bait)) continue;

                EntityAlive nearest = null;
                float bestSq = float.MaxValue;
                for (int j = 0; j < entities.Count; j++)
                {
                    var a = entities[j] as EntityAlive;
                    if (a == null || a is EntityPlayer || a.IsDead() || !IsLurable(a)) continue;
                    float d = a.GetDistanceSq(bait);
                    if (d < bestSq) { bestSq = d; nearest = a; }
                }

                string who = "no lurable zombie loaded";
                if (nearest != null)
                {
                    EAIApproachDistraction task = null;
                    if (nearest.aiManager != null && nearest.aiManager.tasks != null)
                        task = nearest.aiManager.tasks.GetTask<EAIApproachDistraction>();

                    who = string.Format("{0} #{1} at {2:0.0}m hasTask={3} pending={4} eating={5} target={6}",
                        EntityClassNameOf(nearest), nearest.entityId, Mathf.Sqrt(bestSq), task != null,
                        nearest.pendingDistraction == bait, nearest.distraction == bait,
                        nearest.GetAttackTarget() != null ? nearest.GetAttackTarget().EntityName : "none");
                }

                Log.Out(string.Format("[ZombieCompanion] gore #{0} lifetime={1} eatTicks={2} collided={3} radiusSq={4} | {5}",
                    bait.entityId, bait.distractionLifetime, bait.distractionEatTicks,
                    bait.isCollided, bait.distractionRadiusSq, who));
            }
        }

        private static void PruneMissing<T>(World world, Dictionary<int, T> map)
        {
            List<int> gone = null;
            foreach (var kv in map)
            {
                Entity e = world.GetEntity(kv.Key);
                if (e == null || e.IsDead())
                {
                    (gone ?? (gone = new List<int>())).Add(kv.Key);
                }
            }
            if (gone == null) return;
            for (int i = 0; i < gone.Count; i++) map.Remove(gone[i]);
        }

        private static void PruneStaleIds(World world, HashSet<int> set, int threshold)
        {
            if (set.Count <= threshold) return;
            var gone = new List<int>();
            foreach (int id in set)
            {
                if (world.GetEntity(id) == null) gone.Add(id);
            }
            for (int i = 0; i < gone.Count; i++) set.Remove(gone[i]);
        }

        public static Dictionary<int, ThrallState> LiveThralls { get { return Thralls; } }

        /// <summary>Hostiles currently held on a thrall. Diagnostics only.</summary>
        public static int TauntedCount { get { return TauntedUntil.Count; } }
    }

    // ====================================================================== patches

    /// <summary>
    /// Fires just before vanilla decides whether the zombie can go and eat a decoy. This is where
    /// an active chase gets broken - see ThrallRuntime.OnDistractionConsidered.
    /// </summary>
    [HarmonyPatch(typeof(EAIApproachDistraction), "CanExecute")]
    public static class Patch_ApproachDistraction_CanExecute
    {
        [HarmonyPrefix]
        public static void Prefix(EAIApproachDistraction __instance)
        {
            ThrallRuntime.Guard("ApproachDistraction.CanExecute", () =>
                ThrallRuntime.OnDistractionConsidered(__instance.theEntity));
        }
    }

    /// <summary>
    /// Fires after vanilla has (possibly) decremented distractionEatTicks for this frame, so the
    /// meal-finished edge is visible here.
    /// </summary>
    [HarmonyPatch(typeof(EAIApproachDistraction), "Update")]
    public static class Patch_ApproachDistraction_Update
    {
        [HarmonyPostfix]
        public static void Postfix(EAIApproachDistraction __instance)
        {
            ThrallRuntime.Guard("ApproachDistraction.Update", () =>
            {
                EntityAlive e = __instance.theEntity;
                if (e == null) return;
                ThrallRuntime.OnEatTick(e, e.distraction);
            });
        }
    }

    /// <summary>
    /// The single choke point for "what is this thing allowed to attack". Everything in the game
    /// that assigns a target goes through here, which is why the pacify window and the
    /// never-attack-a-player rule live on this method rather than on individual AI tasks.
    /// </summary>
    [HarmonyPatch(typeof(EntityAlive), "SetAttackTarget")]
    public static class Patch_EntityAlive_SetAttackTarget
    {
        [HarmonyPrefix]
        public static bool Prefix(EntityAlive __instance, EntityAlive _attackTarget)
        {
            try
            {
                return ThrallRuntime.AllowAttackTarget(__instance, _attackTarget);
            }
            catch (Exception)
            {
                return true; // never let this mod stop the game assigning targets
            }
        }
    }

    /// <summary>
    /// This is the method that builds an entity's AI task lists out of its EntityClass, so it is
    /// the exact moment the lists exist and are complete. Patching EntityAlive.Init instead would
    /// be a coin flip on whether aiManager had been populated yet.
    /// </summary>
    [HarmonyPatch(typeof(EAIManager), "CopyPropertiesFromEntityClass")]
    public static class Patch_EAIManager_CopyProperties
    {
        [HarmonyPostfix]
        public static void Postfix(EAIManager __instance)
        {
            if (!ThrallRuntime.IsServer) return;
            ThrallRuntime.Guard("EAIManager.CopyPropertiesFromEntityClass", () =>
                ThrallRuntime.EnsureDistractionTask(__instance));
        }
    }

    /// <summary>
    /// Keeps thralls out of the save.
    ///
    /// EntityEnemy.IsSavedToFile returns false only for Dynamic-spawned zombies, and a thrall is
    /// deliberately StaticSpawner so the horde manager cannot cull it - so without this it would be
    /// written into its chunk and come back on the next load as a plain hostile zombie standing
    /// wherever the player last was. The mod's own store is the one authority on who owns what;
    /// the world file must not hold a second, stale opinion.
    /// </summary>
    [HarmonyPatch(typeof(EntityEnemy), "IsSavedToFile")]
    public static class Patch_EntityEnemy_IsSavedToFile
    {
        [HarmonyPrefix]
        public static bool Prefix(EntityEnemy __instance, ref bool __result)
        {
            try
            {
                if (!ThrallRuntime.IsThrall(__instance.entityId)) return true;
                __result = false;
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Makes a thrall's victim fight back - see ThrallRuntime.TeachToFightBack for why that needs
    /// help at all. Postfix rather than prefix so the damage has already landed and a killing blow
    /// does not bother waking anything up.
    /// </summary>
    [HarmonyPatch(typeof(EntityAlive), "DamageEntity")]
    public static class Patch_EntityAlive_DamageEntity
    {
        [HarmonyPostfix]
        public static void Postfix(EntityAlive __instance, DamageSource _damageSource)
        {
            // Every point of damage in the game passes through here, so the cheap rejections are
            // done inline. Only a hit that a thrall actually landed pays for the closure the Guard
            // needs, which in practice is a handful of calls a second at most.
            if (!ZcConfig.ThrallsDrawAggro) return;
            if (__instance == null || _damageSource == null) return;
            if (!(__instance is EntityEnemy) || __instance.IsDead()) return;

            int attackerId;
            try
            {
                attackerId = _damageSource.getEntityId();
            }
            catch (Exception)
            {
                return;
            }
            if (attackerId < 0 || !ThrallRuntime.IsThrall(attackerId)) return;

            ThrallRuntime.Guard("EntityAlive.DamageEntity", () =>
            {
                if (!ThrallRuntime.IsServer) return;

                var world = GameManager.Instance != null ? GameManager.Instance.World : null;
                if (world == null) return;

                var thrall = world.GetEntity(attackerId) as EntityAlive;
                ThrallRuntime.Taunt(__instance, thrall);
            });
        }
    }
}
