using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ChaoticBeastmaster
{
    /// <summary>State the mod keeps about one animal that is part-way through being tamed.</summary>
    public class BaitState
    {
        /// <summary>Entity id of the player whose bait this animal is working on.</summary>
        public int FeederEntityId = -1;
        /// <summary>Time.time before which this animal may not target a player.</summary>
        public float PacifyUntil;
        /// <summary>Finished meals so far, in Prime-adjusted units.</summary>
        public int Feeds;
    }

    /// <summary>Who is currently working on one bait item. animal entity id -> last seen time.</summary>
    public class BaitClaim
    {
        public readonly Dictionary<int, float> Animals = new Dictionary<int, float>();
    }

    /// <summary>A tamed animal currently alive in the world.</summary>
    public class PetState
    {
        public int PetEntityId;
        public int OwnerEntityId = -1;
        /// <summary>Stable cross-session owner key (PlatformUserIdentifierAbs.CombinedString).</summary>
        public string OwnerKey;
        public string EntityClassName;
    }

    public static class TameRuntime
    {
        /// <summary>Tag every bait item carries (items.xml). Cheaper than comparing item names.</summary>
        private static FastTags<TagGroup.Global> baitTag;
        /// <summary>Tag that marks an animal as tameable (entityclasses.xml).</summary>
        private static FastTags<TagGroup.Global> tameableTag;
        /// <summary>Tag that marks an animal as lurable but never tameable - the undead ones.</summary>
        private static FastTags<TagGroup.Global> baitableTag;
        private static bool tagsReady;

        /// <summary>Animals part-way through taming, keyed by animal entity id.</summary>
        private static readonly Dictionary<int, BaitState> Baited = new Dictionary<int, BaitState>();

        /// <summary>Live companions, keyed by animal entity id.</summary>
        private static readonly Dictionary<int, PetState> Pets = new Dictionary<int, PetState>();

        /// <summary>Bait items already scored, so one slab cannot count twice.</summary>
        private static readonly HashSet<int> ConsumedBait = new HashSet<int>();

        /// <summary>Which animals have laid claim to a given bait item, keyed by bait entity id.</summary>
        private static readonly Dictionary<int, BaitClaim> BaitClaims = new Dictionary<int, BaitClaim>();

        /// <summary>Animals we have already given the ApproachDistraction task to.</summary>
        private static readonly HashSet<int> TaskInjected = new HashSet<int>();

        private static readonly HashSet<string> LoggedOnce = new HashSet<string>();

        private static float nextSweep;

        private static void EnsureTags()
        {
            if (tagsReady) return;
            baitTag = FastTags<TagGroup.Global>.Parse("chaoticBait");
            tameableTag = FastTags<TagGroup.Global>.Parse("chaoticTameable");
            baitableTag = FastTags<TagGroup.Global>.Parse("chaoticTameable,chaoticBaitable");
            tagsReady = true;
        }

        /// <summary>
        /// The animals the mod guarantees. entityclasses.xml carries the same list and is still the
        /// place to add more - this is the floor under it, not a replacement.
        /// </summary>
        public static readonly string[] DefaultTameable =
            { "animalWolf", "animalCoyote", "animalMountainLion", "animalBear", "animalBearSmall" };
        public static readonly string[] DefaultBaitable =
            { "animalDireWolf", "animalZombieBear", "animalZombieDog", "animalZombieBoar" };

        /// <summary>Animals whose tag the XML supplied. Diagnostics only.</summary>
        private static readonly HashSet<string> TagFromXml = new HashSet<string>();
        /// <summary>Animals whose tag this code had to put on. Diagnostics only.</summary>
        private static readonly HashSet<string> TagForced = new HashSet<string>();

        public static bool TagCameFromXml(string className) { return TagFromXml.Contains(className); }
        public static bool TagWasForced(string className) { return TagForced.Contains(className); }
        public static int ForcedTagCount { get { return TagForced.Count; } }

        /// <summary>
        /// Puts the tags on the entity classes from code, instead of trusting the xpath in
        /// entityclasses.xml to have landed.
        ///
        /// The xpath is a single point of failure the mod cannot see fail: any other mod that
        /// op="set"s an animal's Tags after us wipes ours (load order is alphabetical, so almost
        /// everything loads after "ChaoticBeastmaster"), and a TFP retune that moves Tags onto a
        /// template makes it a silent no-op. Either way every animal in the world goes deaf to
        /// bait and the only symptom is "the mod does nothing" - which is exactly the bug this
        /// fixes. Vanilla reads EntityClass.list[...].Tags at the moment it tests the bait
        /// (EntityItem.tickDistraction), not a copy taken at spawn, so setting it here is enough.
        ///
        /// Idempotent, and additive: an animal that already carries the tag is left alone, so the
        /// XML keeps working as the extension point for animals not on the default list.
        /// </summary>
        public static void ApplyAnimalTags()
        {
            EnsureTags();
            TagFromXml.Clear();
            TagForced.Clear();

            var wanted = new Dictionary<string, FastTags<TagGroup.Global>>();
            FastTags<TagGroup.Global> tameOne = FastTags<TagGroup.Global>.GetTag("chaoticTameable");
            FastTags<TagGroup.Global> baitOne = FastTags<TagGroup.Global>.GetTag("chaoticBaitable");

            for (int i = 0; i < DefaultTameable.Length; i++) wanted[DefaultTameable[i]] = tameOne;
            for (int i = 0; i < DefaultBaitable.Length; i++) wanted[DefaultBaitable[i]] = baitOne;
            AddNames(wanted, BmConfig.ExtraTameable, tameOne);
            AddNames(wanted, BmConfig.ExtraBaitable, baitOne);

            var missing = new List<string>(wanted.Keys);

            foreach (var kv in EntityClass.list.Dict)
            {
                EntityClass ec = kv.Value;
                if (ec == null || string.IsNullOrEmpty(ec.entityClassName)) continue;

                FastTags<TagGroup.Global> want;
                if (!wanted.TryGetValue(ec.entityClassName, out want)) continue;
                missing.Remove(ec.entityClassName);

                if (ec.Tags.Test_AnySet(want))
                {
                    TagFromXml.Add(ec.entityClassName);
                    continue;
                }

                ec.Tags = ec.Tags | want;
                TagForced.Add(ec.entityClassName);
            }

            if (TagForced.Count > 0)
            {
                // Worth a warning even though the mod now works: it means something in this
                // install is eating the xpath, and whatever that is will eat the next one too.
                Log.Warning("[Beastmaster] entityclasses.xml did not tag "
                    + TagForced.Count + " animal(s) - applied from code instead: "
                    + string.Join(", ", new List<string>(TagForced).ToArray())
                    + ". Another mod is most likely overwriting the Tags property.");
            }

            if (missing.Count > 0)
            {
                Log.Warning("[Beastmaster] entity class(es) not present in this install, skipped: "
                    + string.Join(", ", missing.ToArray()));
            }
        }

        private static void AddNames(Dictionary<string, FastTags<TagGroup.Global>> into, string csv,
                                     FastTags<TagGroup.Global> tag)
        {
            if (string.IsNullOrEmpty(csv)) return;
            string[] parts = csv.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string n = parts[i].Trim();
                if (n.Length > 0) into[n] = tag;
            }
        }

        /// <summary>For the self-check, which needs the parsed tags before any animal has spawned.</summary>
        public static void EnsureTagsPublic() { EnsureTags(); }
        public static FastTags<TagGroup.Global> TameableTag { get { EnsureTags(); return tameableTag; } }
        public static FastTags<TagGroup.Global> BaitableTag { get { EnsureTags(); return baitableTag; } }

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
                    Log.Error("[Beastmaster] " + site + " failed (further errors here suppressed): " + e);
                }
            }
        }

        public static void DebugLog(string msg)
        {
            if (BmConfig.Debug) Log.Out("[Beastmaster] " + msg);
        }

        // ------------------------------------------------------------------ queries

        public static bool IsBaitItem(EntityItem item)
        {
            if (item == null || item.itemClass == null) return false;
            EnsureTags();
            return item.itemClass.ItemTags.Test_AnySet(baitTag);
        }

        /// <summary>Can be fed to the point of following you. Living predators only.</summary>
        public static bool IsTameable(EntityAlive e)
        {
            return HasTag(e, tameableTag);
        }

        /// <summary>
        /// Responds to bait at all. Superset of IsTameable: the undead animals (dire wolf, zombie
        /// bear/dog/boar) are lurable so you can escape one, but never tameable.
        /// </summary>
        public static bool IsBaitable(EntityAlive e)
        {
            return HasTag(e, baitableTag);
        }

        private static bool HasTag(EntityAlive e, FastTags<TagGroup.Global> tags)
        {
            if (e == null) return false;
            EnsureTags();
            EntityClass ec;
            if (!EntityClass.list.TryGetValue(e.entityClass, out ec) || ec == null) return false;
            return ec.Tags.Test_AnySet(tags);
        }

        public static bool IsPet(EntityAlive e)
        {
            return e != null && Pets.ContainsKey(e.entityId);
        }

        public static PetState GetPet(int entityId)
        {
            PetState p;
            return Pets.TryGetValue(entityId, out p) ? p : null;
        }

        public static EntityPlayer GetOwnerOf(EntityAlive pet)
        {
            if (pet == null) return null;
            PetState st;
            if (!Pets.TryGetValue(pet.entityId, out st)) return null;
            if (st.OwnerEntityId < 0) return null;

            var world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return null;

            var owner = world.GetEntity(st.OwnerEntityId) as EntityPlayer;
            if (owner == null || owner.IsDead()) return null;
            return owner;
        }

        // ------------------------------------------------------------------ AI task injection

        /// <summary>
        /// Gives a tameable animal the AI task that makes it walk to and eat a decoy.
        /// Vanilla only ships this task on the zombie animals, so without it the bait lands and
        /// nothing happens. Injected at spawn rather than declared in XML so this mod never has
        /// to restate (and therefore never has to keep in sync) a vanilla AITask list.
        ///
        /// Priority 0 puts it above every vanilla task including ApproachAndAttackTarget, which
        /// matches how vanilla orders it on animalZombieBear - the whole point is that it wins
        /// against the chase.
        /// </summary>
        public static void EnsureDistractionTask(EAIManager ai)
        {
            if (ai == null) return;
            // Taken off the manager, not off EntityAlive.aiManager: this runs while the manager is
            // still being built, and the entity's back-reference to it may not be set yet.
            EntityAlive e = ai.entity;
            if (e == null) return;
            if (!TaskInjected.Add(e.entityId)) return;
            // Baitable, not tameable: Zombie Dog and Zombie Boar have no ApproachDistraction task
            // in vanilla at all, so without this they would ignore bait entirely.
            if (!IsBaitable(e)) return;

            var tasks = ai.tasks;
            if (tasks == null) return;
            if (tasks.GetTask<EAIApproachDistraction>() != null) return;

            var task = new EAIApproachDistraction();
            task.Init(e);
            tasks.AddTask(0, task);

            DebugLog("gave ApproachDistraction to " + e.EntityName + " (" + e.entityId + ")");
        }

        /// <summary>
        /// Belt and braces for the spawn-time injection above.
        ///
        /// EnsureDistractionTask only ever fires on the one frame an entity is built, so anything
        /// that misses that window - an animal already standing in the world when the patches were
        /// applied, or any spawn path that does not run CopyPropertiesFromEntityClass - is deaf to
        /// bait for the rest of its life, with no symptom other than "the mod does nothing".
        /// Sweeping once a second closes that hole; TaskInjected means each animal is only ever
        /// considered once, so the steady-state cost is one dictionary probe per entity per second.
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
        /// Called every time the animal's ApproachDistraction task is asked whether it can run.
        ///
        /// Vanilla's CanExecute bails out while the animal holds an attack target, and only keeps
        /// the pending bait rather than acting on it. That is precisely the case this mod exists
        /// for - meat thrown at a wolf that is already on you. So if the bait is ours and the
        /// animal is hunting a player, drop the target here, before vanilla looks at it, and hold
        /// a short window where it may not re-acquire.
        ///
        /// The window only has to cover the gap until EAIApproachDistraction.Start() sets
        /// theEntity.distraction, because from that moment vanilla suppresses re-targeting by
        /// itself (EAISetNearestEntityAsTarget.CanExecute returns false while distraction != null).
        /// </summary>
        public static void OnDistractionConsidered(EntityAlive animal)
        {
            if (animal == null || !IsServer) return;

            EntityItem bait = animal.pendingDistraction;
            if (!IsBaitItem(bait))
            {
                if (bait != null) DebugLog(animal.EntityName + " considered a non-bait distraction");
                return;
            }
            // Baitable, not tameable - breaking a dire wolf or zombie bear off you is the whole
            // reason those two are in scope, even though they can never be kept.
            if (!IsBaitable(animal))
            {
                DebugLog(animal.EntityName + " (" + animal.entityId + ") saw bait but carries no chaotic tag");
                return;
            }

            DebugLog(animal.EntityName + " (" + animal.entityId + ") considering bait " + bait.entityId
                + " target=" + (animal.GetAttackTarget() != null ? animal.GetAttackTarget().EntityName : "none"));

            // One slab feeds one animal. Vanilla pulses a decoy at every eligible entity in
            // radius, so without this an entire wolf pack peels onto a single piece of meat -
            // and since a feed is scored per BAIT, only the first one to finish would get credit
            // anyway. Losing the claim means this animal keeps hunting you.
            if (!TryClaimBait(animal, bait))
            {
                animal.pendingDistraction = null;
                return;
            }

            BaitState st = GetOrCreateBaitState(animal.entityId);

            // belongsPlayerId is set to the thrower's entity id by GameManager.ItemDropServer,
            // which is what ItemActionThrowAway calls. That is our feeder.
            if (bait.belongsPlayerId >= 0) st.FeederEntityId = bait.belongsPlayerId;

            if (!BmConfig.BreakActiveAggro) return;

            var target = animal.GetAttackTarget();
            if (target is EntityPlayer)
            {
                st.PacifyUntil = Time.time + BmConfig.PacifySeconds;
                animal.SetAttackTarget(null, 0);
                DebugLog(animal.EntityName + " (" + animal.entityId + ") broke off its chase for bait");
            }
        }

        /// <summary>
        /// Decides whether this animal is allowed to work on this particular bait.
        ///
        /// Claims are per bait item, capped at BmConfig.MaxAnimalsPerBait (1 by default). A closer
        /// animal can take the claim off a further one, but only while that one is still walking
        /// over - once it has actually committed (distraction == bait) it keeps the meal. Without
        /// the distance rule the winner would just be whichever animal's AI ticked first, which
        /// would routinely hand the meat to a wolf 25m away while the one on top of you kept
        /// biting.
        /// </summary>
        private static bool TryClaimBait(EntityAlive animal, EntityItem bait)
        {
            int max = BmConfig.MaxAnimalsPerBait;
            if (max <= 0) return true; // 0 = unlimited, vanilla pack behaviour

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
            foreach (var kv in claim.Animals)
            {
                if (kv.Key == animal.entityId) continue;
                var a = world.GetEntity(kv.Key) as EntityAlive;
                bool stillOnIt = a != null && !a.IsDead()
                                 && (a.distraction == bait || a.pendingDistraction == bait);
                if (!stillOnIt && now - kv.Value > 3f) (gone ?? (gone = new List<int>())).Add(kv.Key);
            }
            if (gone != null) for (int i = 0; i < gone.Count; i++) claim.Animals.Remove(gone[i]);

            if (claim.Animals.ContainsKey(animal.entityId))
            {
                claim.Animals[animal.entityId] = now;
                return true;
            }

            if (claim.Animals.Count < max)
            {
                claim.Animals[animal.entityId] = now;
                return true;
            }

            // Full. Take it off the furthest incumbent if we are closer and it has not started
            // eating yet.
            float myDistSq = animal.GetDistanceSq(bait);
            int evict = -1;
            float worstDistSq = myDistSq;

            foreach (var kv in claim.Animals)
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
            claim.Animals.Remove(evict);
            claim.Animals[animal.entityId] = now;

            DebugLog(animal.EntityName + " (" + animal.entityId + ") took bait " + bait.entityId
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
        /// Two jobs: hold the pacify window open while a baited animal disengages, and make a
        /// companion structurally incapable of turning on a player.
        /// </summary>
        public static bool AllowAttackTarget(EntityAlive self, EntityAlive target)
        {
            // Clearing a target must always work - vanilla's own AI does this constantly, and
            // EAIApproachDistraction.Start() depends on it.
            if (self == null || target == null) return true;
            if (!IsServer) return true;

            if (Pets.ContainsKey(self.entityId))
            {
                if (!BmConfig.PetsAttackPlayers && target is EntityPlayer) return false;

                // Never the owner, whatever the config says. Friendly fire from the person who
                // tamed it is the single most likely way a pet would otherwise turn.
                PetState st = Pets[self.entityId];
                if (target.entityId == st.OwnerEntityId) return false;

                // Never another player's pet.
                if (Pets.ContainsKey(target.entityId)) return false;
                return true;
            }

            BaitState bs;
            if (Baited.TryGetValue(self.entityId, out bs) && Time.time < bs.PacifyUntil && target is EntityPlayer)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Called from the tail of EAIApproachDistraction.Update, which is where vanilla decrements
        /// distractionEatTicks while the animal has its head down. When that counter runs out the
        /// meal is finished (EntityItem.OnUpdateEntity kills the item on the same condition), so
        /// that is the moment a feed is scored - not when the animal merely arrives.
        /// </summary>
        public static void OnEatTick(EntityAlive animal, EntityItem bait)
        {
            if (animal == null || bait == null || !IsServer) return;
            if (!IsBaitItem(bait)) return;
            if (bait.distractionEatTicks > 0) return;

            // One slab, one feed, no matter how many ticks land on this frame.
            if (!ConsumedBait.Add(bait.entityId)) return;

            if (Pets.ContainsKey(animal.entityId))
            {
                HealPet(animal, bait);
                return;
            }

            // Baitable but not tameable - the undead. It ate, it was busy, you got away, and that
            // is all it will ever do. Say so, otherwise the player just sees a counter that never
            // moves and assumes the mod is broken.
            if (!IsTameable(animal))
            {
                BaitState undead = GetOrCreateBaitState(animal.entityId);
                if (bait.belongsPlayerId >= 0) undead.FeederEntityId = bait.belongsPlayerId;
                var whoFed = FeederOf(undead);
                if (BmConfig.NotifyProgress && whoFed != null)
                {
                    GameManager.ShowTooltipMP(whoFed,
                        FriendlyName(animal) + " tears into it. Dead things do not make friends - run.", null);
                }
                return;
            }

            BaitState st = GetOrCreateBaitState(animal.entityId);
            int worth = FeedValueOf(bait);
            st.Feeds += worth;

            var feeder = FeederOf(st);
            string beast = FriendlyName(animal);

            if (st.Feeds >= BmConfig.FeedsToTame)
            {
                Tame(animal, feeder, st);
                return;
            }

            if (BmConfig.NotifyProgress && feeder != null)
            {
                GameManager.ShowTooltipMP(feeder,
                    beast + " eats, watching you the whole time. (" + st.Feeds + "/" + BmConfig.FeedsToTame + ")", null);
            }

            DebugLog(beast + " (" + animal.entityId + ") fed " + st.Feeds + "/" + BmConfig.FeedsToTame);
        }

        private static int FeedValueOf(EntityItem bait)
        {
            if (bait.itemClass == null) return 1;
            return bait.itemClass.Name == "chaoticBaitPrime" ? BmConfig.PrimeBaitFeedValue : 1;
        }

        private static EntityPlayer FeederOf(BaitState st)
        {
            if (st == null || st.FeederEntityId < 0) return null;
            var world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return null;
            return world.GetEntity(st.FeederEntityId) as EntityPlayer;
        }

        /// <summary>Bait fed to an animal that is already yours patches it up instead.</summary>
        private static void HealPet(EntityAlive pet, EntityItem bait)
        {
            int max = pet.GetMaxHealth();
            bool prime = FeedValueOf(bait) >= BmConfig.PrimeBaitFeedValue;
            int heal = Mathf.Max(1, Mathf.RoundToInt(max * (prime ? 0.5f : 0.25f)));
            // AddHealth clamps to max internally, so no need to work out the shortfall first.
            pet.AddHealth(heal);

            var owner = GetOwnerOf(pet);
            if (owner != null && BmConfig.NotifyProgress)
            {
                GameManager.ShowTooltipMP(owner, FriendlyName(pet) + " wolfs it down. (" + pet.Health + "/" + max + " HP)", null);
            }
        }

        // ------------------------------------------------------------------ taming

        private static void Tame(EntityAlive animal, EntityPlayer owner, BaitState st)
        {
            Baited.Remove(animal.entityId);

            if (owner == null)
            {
                // Fed to the threshold by someone who has since died or logged off. Reset rather
                // than tame to nobody, otherwise the animal is stuck in limbo.
                DebugLog("tame threshold reached but the feeder is gone; resetting " + animal.entityId);
                return;
            }

            string ownerKey = OwnerKeyOf(owner);
            if (string.IsNullOrEmpty(ownerKey))
            {
                DebugLog("no stable owner key for " + owner.EntityName + "; taming without persistence");
            }

            // The kennel is what limits you, not the field. Full means you have to let one go
            // before another will follow you - the animal stays wild and its progress resets.
            if (!string.IsNullOrEmpty(ownerKey) && PetStore.Count(ownerKey) >= BmConfig.MaxOwnedPets)
            {
                GameManager.ShowTooltipMP(owner,
                    FriendlyName(animal) + " will not come with you - you are already keeping "
                    + BmConfig.MaxOwnedPets + ". Use /pet release <n> first.", null);
                return;
            }

            // Only ever one out at a time: whatever is currently at your side steps back into the
            // kennel to make room for the new one.
            string stowed = null;
            var wasOut = PetStore.GetActive(ownerKey);
            if (wasOut != null && HasLivePet(ownerKey))
            {
                stowed = PrettyClassName(wasOut.EntityClassName);
                DespawnLivePetOf(ownerKey);
            }

            if (!string.IsNullOrEmpty(ownerKey)
                && !PetStore.Add(ownerKey, EntityClassNameOf(animal), true))
            {
                GameManager.ShowTooltipMP(owner, FriendlyName(animal) + " will not come with you - kennel full.", null);
                return;
            }

            MakeCompanion(animal, owner, ownerKey);

            GameManager.ShowTooltipMP(owner,
                FriendlyName(animal) + " stops eating, looks up at you, and stays. It is yours now."
                + (stowed != null ? "\nYour " + stowed + " waits in the kennel - /pet list" : ""), null);

            Log.Out("[Beastmaster] " + owner.EntityName + " tamed a " + EntityClassNameOf(animal)
                + " (entity " + animal.entityId + ")");
        }

        /// <summary>True if this owner currently has a companion standing in the world.</summary>
        public static bool HasLivePet(string ownerKey)
        {
            if (string.IsNullOrEmpty(ownerKey)) return false;
            foreach (var kv in Pets) if (kv.Value.OwnerKey == ownerKey) return true;
            return false;
        }

        /// <summary>
        /// Takes an owner's companion out of the world without killing it. Used by /pet stow,
        /// by /pet call before bringing a different one out, and on logout.
        ///
        /// Despawned, not Killed: EnumRemoveEntityReason.Killed fires the EntityKilled event, and
        /// that handler deletes the saved record - so stowing would permanently destroy the pet.
        /// </summary>
        public static void DespawnLivePetOf(string ownerKey)
        {
            if (string.IsNullOrEmpty(ownerKey)) return;

            var world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return;

            int id = -1;
            foreach (var kv in Pets) { if (kv.Value.OwnerKey == ownerKey) { id = kv.Key; break; } }
            if (id < 0) return;

            Pets.Remove(id);
            world.RemoveEntity(id, EnumRemoveEntityReason.Despawned);
            DebugLog("stowed companion " + id);
        }

        /// <summary>
        /// Turns a wild animal into a companion in place - no despawn/respawn, so the same beast
        /// you fed is the one that follows you.
        ///
        /// The rewiring is all done on vanilla AI objects rather than by adding custom combat AI:
        /// the animal already knows how to chase and maul, it just has the wrong list of things it
        /// is willing to chase and maul. Swapping those lists is both less code and better
        /// behaved than a bespoke attack task.
        /// </summary>
        public static void MakeCompanion(EntityAlive animal, EntityPlayer owner, string ownerKey)
        {
            var st = new PetState
            {
                PetEntityId = animal.entityId,
                OwnerEntityId = owner != null ? owner.entityId : -1,
                OwnerKey = ownerKey,
                EntityClassName = EntityClassNameOf(animal)
            };
            Pets[animal.entityId] = st;

            animal.SetAttackTarget(null, 0);

            var ai = animal.aiManager;
            if (ai == null) return;

            // What it will chase: everything hostile, and no longer players.
            var attack = ai.tasks != null ? ai.tasks.GetTask<EAIApproachAndAttackTarget>() : null;
            if (attack != null)
            {
                attack.targetClasses = new List<EAIApproachAndAttackTarget.TargetClass>
                {
                    NewChaseClass(typeof(EntityZombie), 60f),
                    NewChaseClass(typeof(EntityBandit), 60f),
                    NewChaseClass(typeof(EntityEnemyAnimal), 30f),
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
                    NewSeekClass(typeof(EntityZombie), 30f, 25f),
                    NewSeekClass(typeof(EntityBandit), 30f, 25f),
                };
                acquire.playerTargetClassIndex = -1;
            }

            // A companion that runs away at low health, wanders off, or returns to a "territory"
            // is not a companion. Strip those.
            RemoveTask<EAIRunawayWhenHurt>(ai.tasks);
            RemoveTask<EAITerritorial>(ai.tasks);
            RemoveTask<EAIWander>(ai.tasks);
            RemoveTask<EAIApproachSpot>(ai.tasks);

            // Priority 5 sits below ApproachAndAttackTarget (4 on every animal this mod tames), so
            // fighting always beats heeling, and above nothing else now that Wander is gone.
            if (ai.tasks != null && ai.tasks.GetTask<EAIChaoticFollowOwner>() == null)
            {
                var follow = new EAIChaoticFollowOwner();
                follow.Init(animal);
                ai.tasks.AddTask(5, follow);
            }

            // Stat.Max is read-only and derived; BaseMax is the settable input GetMaxHealth()
            // ultimately reads through Stats.Health.Max.
            if (BmConfig.PetHealthScale > 1f && animal.Stats != null && animal.Stats.Health != null)
            {
                animal.Stats.Health.BaseMax = animal.Stats.Health.BaseMax * BmConfig.PetHealthScale;
            }

            // Taming heals it up: it just ate five meals, and a pet that arrives on 12 HP because
            // you shot it before you thought better of it is a pet that dies to the first zombie.
            int missing = animal.GetMaxHealth() - animal.Health;
            if (missing > 0) animal.AddHealth(missing);

            if (owner != null)
            {
                animal.SetEntityName(owner.EntityName + "'s " + FriendlyName(animal));
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

        // ------------------------------------------------------------------ naming / ids

        public static string EntityClassNameOf(EntityAlive e)
        {
            EntityClass ec;
            if (e != null && EntityClass.list.TryGetValue(e.entityClass, out ec) && ec != null) return ec.entityClassName;
            return "animalWolf";
        }

        /// <summary>"animalMountainLion" -> "Mountain Lion", for tooltips.</summary>
        public static string FriendlyName(EntityAlive e)
        {
            return PrettyClassName(EntityClassNameOf(e));
        }

        /// <summary>Same, for a class name we only have as a string (saved records).</summary>
        public static string PrettyClassName(string className)
        {
            string n = className ?? "";
            if (n.StartsWith("animal", StringComparison.OrdinalIgnoreCase)) n = n.Substring(6);
            if (n.Length == 0) return "Companion";

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
        ///   1. The ClientInfo. What every earlier build used, and kept first so that pets tamed
        ///      by one of those builds keep the key they were saved under.
        ///   2. The persistent player list. The HOST of a client-hosted game has no ClientInfo at
        ///      all - they are not a network client of their own server - so ForEntityId returns
        ///      null and step 1 gave up. That is why, before 1.2.3, a host could tame an animal
        ///      but never keep it across a restart and never use /pet.
        ///   3. persistentLocalPlayer, for the moment early in a host's session before their
        ///      entity id has been mapped into the list.
        /// Null only if all three fail, which means taming still works but will not persist.
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
            PetRespawner.OnPlayerSpawned(entityId, respawnType);
        }

        public static void OnPlayerLeft(ClientInfo ci)
        {
            if (!IsServer || ci == null) return;
            PetRespawner.OnPlayerLeft(ci);
        }

        public static void OnEntityKilled(Entity killed, Entity killer)
        {
            if (!IsServer || killed == null) return;

            PetState st;
            if (Pets.TryGetValue(killed.entityId, out st))
            {
                Pets.Remove(killed.entityId);
                // Only the one that was out is lost; anything in the kennel is untouched.
                PetStore.ForgetActive(st.OwnerKey);

                var world = GameManager.Instance != null ? GameManager.Instance.World : null;
                var owner = world != null ? world.GetEntity(st.OwnerEntityId) as EntityPlayer : null;
                if (owner != null)
                {
                    GameManager.ShowTooltipMP(owner, FriendlyName(killed as EntityAlive) + " died protecting you.", null);
                }
                Log.Out("[Beastmaster] pet " + st.EntityClassName + " (" + killed.entityId + ") died");
            }

            Baited.Remove(killed.entityId);
            TaskInjected.Remove(killed.entityId);
        }

        public static void OnShutdown()
        {
            PetStore.Flush();
            Baited.Clear();
            Pets.Clear();
            ConsumedBait.Clear();
            TaskInjected.Clear();
        }

        /// <summary>
        /// Once a second: drop bookkeeping for entities that no longer exist, and re-point live
        /// pets at their owner's current entity id (which changes on every respawn and relog).
        /// Without the sweep these dictionaries would grow for the life of the server, because
        /// animals that despawn on chunk unload never raise EntityKilled.
        /// </summary>
        public static void Tick()
        {
            if (!IsServer) return;
            if (Time.time < nextSweep) return;
            nextSweep = Time.time + 1f;

            var world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return;

            PetRespawner.Tick(world);
            SweepDistractionTasks(world);
            if (BmConfig.Debug) TraceLiveBait(world);

            PruneMissing(world, Baited);
            PruneMissing(world, Pets);

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

            if (TaskInjected.Count > 2048)
            {
                var gone = new List<int>();
                foreach (int id in TaskInjected)
                {
                    if (world.GetEntity(id) == null) gone.Add(id);
                }
                for (int i = 0; i < gone.Count; i++) TaskInjected.Remove(gone[i]);
            }
        }

        /// <summary>
        /// One line a second, per bait lying in the world, pairing it with the nearest baitable
        /// animal. Every link in the chain is on that line, so whichever one is broken is visible
        /// without a debugger: the bait's own state (does it still live, has it landed, does it
        /// still pulse), and the animal's (does it carry the AI task, has the pulse reached it,
        /// is it still locked on the player).
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
                    if (a == null || a is EntityPlayer || a.IsDead() || !IsBaitable(a)) continue;
                    float d = a.GetDistanceSq(bait);
                    if (d < bestSq) { bestSq = d; nearest = a; }
                }

                string animal = "no baitable animal loaded";
                if (nearest != null)
                {
                    EAIApproachDistraction task = null;
                    if (nearest.aiManager != null && nearest.aiManager.tasks != null)
                        task = nearest.aiManager.tasks.GetTask<EAIApproachDistraction>();

                    animal = string.Format("{0} #{1} at {2:0.0}m hasTask={3} pending={4} eating={5} target={6}",
                        EntityClassNameOf(nearest), nearest.entityId, Mathf.Sqrt(bestSq), task != null,
                        nearest.pendingDistraction == bait, nearest.distraction == bait,
                        nearest.GetAttackTarget() != null ? nearest.GetAttackTarget().EntityName : "none");
                }

                Log.Out(string.Format("[Beastmaster] bait #{0} lifetime={1} eatTicks={2} collided={3} radiusSq={4} | {5}",
                    bait.entityId, bait.distractionLifetime, bait.distractionEatTicks,
                    bait.isCollided, bait.distractionRadiusSq, animal));
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

        public static Dictionary<int, PetState> LivePets { get { return Pets; } }
    }

    // ====================================================================== patches

    /// <summary>
    /// Fires just before vanilla decides whether the animal can go and eat a decoy. This is where
    /// an active chase gets broken - see TameRuntime.OnDistractionConsidered.
    /// </summary>
    [HarmonyPatch(typeof(EAIApproachDistraction), "CanExecute")]
    public static class Patch_ApproachDistraction_CanExecute
    {
        [HarmonyPrefix]
        public static void Prefix(EAIApproachDistraction __instance)
        {
            TameRuntime.Guard("ApproachDistraction.CanExecute", () =>
                TameRuntime.OnDistractionConsidered(__instance.theEntity));
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
            TameRuntime.Guard("ApproachDistraction.Update", () =>
            {
                EntityAlive e = __instance.theEntity;
                if (e == null) return;
                TameRuntime.OnEatTick(e, e.distraction);
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
                return TameRuntime.AllowAttackTarget(__instance, _attackTarget);
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
            if (!TameRuntime.IsServer) return;
            TameRuntime.Guard("EAIManager.CopyPropertiesFromEntityClass", () =>
                TameRuntime.EnsureDistractionTask(__instance));
        }
    }
}
