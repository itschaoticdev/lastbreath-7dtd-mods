using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChaoticZombieCompanion
{
    /// <summary>
    /// Puts thralls back after a relog, a restart, or a chunk unload, and takes them away when
    /// their owner leaves.
    ///
    /// This exists because nothing else will. A thrall is an ordinary zombie entity, and this mod
    /// deliberately keeps it out of the save file (see Patch_EntityEnemy_IsSavedToFile) so it can
    /// never be restored as a hostile. ThrallStore remembers the fact of it; this class re-creates
    /// the thing itself.
    ///
    /// Despawning on logout is the other half of that deal: an unowned thrall standing in the world
    /// would walk into the next horde, die, and delete the player's record while they were offline.
    /// </summary>
    public static class ThrallRespawner
    {
        private class PendingSpawn
        {
            public int OwnerEntityId;
            public string OwnerKey;
            public string EntityClassName;
            /// <summary>Time.time to spawn at. A short delay lets the player's chunks finish loading.</summary>
            public float DueAt;
            public int Attempts;
        }

        private static readonly List<PendingSpawn> Pending = new List<PendingSpawn>();

        /// <summary>Spawn attempts before giving up, so a permanently unloadable spot cannot loop.</summary>
        private const int MaxAttempts = 10;

        /// <summary>Time.time of the next reconcile pass. Cheap, but no reason to run it every tick.</summary>
        private static float nextReconcile;

        public static void OnPlayerSpawned(int entityId, RespawnType respawnType)
        {
            var world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return;

            var player = world.GetEntity(entityId) as EntityPlayer;
            if (player == null) return;

            string ownerKey = ThrallRuntime.OwnerKeyOf(player);
            if (string.IsNullOrEmpty(ownerKey)) return;

            // Re-point already-live thralls instead of spawning duplicates. Happens on death and on
            // teleport, where the player entity is reused or replaced but the thralls never left.
            foreach (var kv in ThrallRuntime.LiveThralls)
            {
                if (kv.Value.OwnerKey == ownerKey) kv.Value.OwnerEntityId = entityId;
            }

            // Everything else is left to the reconcile pass, which is the same question asked
            // continuously rather than only at spawn: does what is standing in the world match what
            // the roster says should be?
            ThrallRuntime.DebugLog(player.EntityName + " spawned (" + respawnType + "), "
                + ThrallStore.ActiveCount(ownerKey) + " thrall(s) marked out");
        }

        /// <summary>
        /// Schedules a thrall to appear beside its owner. The delay exists because on login the
        /// player's chunks are still streaming in, and an entity spawned into an unloaded chunk
        /// falls through the world.
        /// </summary>
        public static void QueueSpawn(EntityPlayer player, string ownerKey, string entityClassName, float delay)
        {
            if (player == null || string.IsNullOrEmpty(ownerKey) || string.IsNullOrEmpty(entityClassName)) return;

            Pending.Add(new PendingSpawn
            {
                OwnerEntityId = player.entityId,
                OwnerKey = ownerKey,
                EntityClassName = entityClassName,
                DueAt = Time.time + delay
            });
        }

        public static void OnPlayerLeft(ClientInfo ci)
        {
            if (ci == null || ci.PlatformId == null) return;
            string ownerKey = ci.PlatformId.CombinedString;

            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                if (Pending[i].OwnerKey == ownerKey) Pending.RemoveAt(i);
            }

            // Despawned, not killed: EnumRemoveEntityReason.Killed would fire EntityKilled, and that
            // handler deletes the saved record - which would mean logging out permanently destroys
            // your thralls. The records stay Active, so they come straight back on the next login.
            ThrallRuntime.DespawnAllOf(ownerKey);
            ThrallRuntime.DebugLog("stored thralls for departing owner " + ownerKey);
        }

        public static void Tick(World world)
        {
            RunPending(world);

            if (Time.time < nextReconcile) return;
            nextReconcile = Time.time + 5f;
            Reconcile(world);
        }

        /// <summary>
        /// The one rule the whole lifecycle hangs off: for every player in the world, what is
        /// standing beside them should equal what their roster says is out.
        ///
        /// Asking it continuously rather than only on login is what covers the cases nothing else
        /// does - a thrall that vanished with its chunk while the owner was driving, a spawn that
        /// failed and was given up on, a server restart where PlayerSpawnedInWorld fired before the
        /// store had finished loading. All of them heal within five seconds and none of them needs
        /// its own code path.
        /// </summary>
        private static void Reconcile(World world)
        {
            var players = world.Players != null ? world.Players.list : null;
            if (players == null) return;

            for (int i = 0; i < players.Count; i++)
            {
                EntityPlayer p = players[i];
                if (p == null || p.IsDead()) continue;

                string ownerKey = ThrallRuntime.OwnerKeyOf(p);
                if (string.IsNullOrEmpty(ownerKey)) continue;

                var wanted = ThrallStore.Actives(ownerKey);
                if (wanted.Count == 0) continue;

                // Count what is already accounted for, per class: live thralls plus anything
                // already queued. Same class twice on the roster means two of them are expected.
                var have = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var kv in ThrallRuntime.LiveThralls)
                {
                    if (kv.Value.OwnerKey != ownerKey) continue;
                    Bump(have, kv.Value.EntityClassName);
                }
                for (int q = 0; q < Pending.Count; q++)
                {
                    if (Pending[q].OwnerKey == ownerKey) Bump(have, Pending[q].EntityClassName);
                }

                for (int w = 0; w < wanted.Count; w++)
                {
                    string cls = wanted[w].EntityClassName;
                    int n;
                    if (have.TryGetValue(cls, out n) && n > 0) { have[cls] = n - 1; continue; }

                    QueueSpawn(p, ownerKey, cls, 3f);
                    ThrallRuntime.DebugLog("queued " + cls + " for " + p.EntityName + " (reconcile)");
                }
            }
        }

        private static void Bump(Dictionary<string, int> map, string key)
        {
            int n;
            map[key] = map.TryGetValue(key, out n) ? n + 1 : 1;
        }

        private static void RunPending(World world)
        {
            if (Pending.Count == 0) return;

            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                PendingSpawn p = Pending[i];
                if (Time.time < p.DueAt) continue;

                var owner = world.GetEntity(p.OwnerEntityId) as EntityPlayer;
                if (owner == null || owner.IsDead())
                {
                    // Owner died or dropped between joining and the thrall arriving. Leave the
                    // saved record alone - reconcile will queue it again once they are back up.
                    Pending.RemoveAt(i);
                    continue;
                }

                // The cap can have been reached since this was queued, e.g. by a second thrall
                // being bound in the field.
                if (ThrallRuntime.LiveCountFor(p.OwnerKey) >= ZcConfig.MaxActiveThralls)
                {
                    Pending.RemoveAt(i);
                    continue;
                }

                if (TrySpawn(world, owner, p))
                {
                    Pending.RemoveAt(i);
                    continue;
                }

                if (++p.Attempts >= MaxAttempts)
                {
                    Log.Warning("[ZombieCompanion] gave up respawning " + p.EntityClassName
                        + " for " + owner.EntityName + " after " + MaxAttempts + " attempts.");
                    Pending.RemoveAt(i);
                    continue;
                }
                p.DueAt = Time.time + 3f;
            }
        }

        private static bool TrySpawn(World world, EntityPlayer owner, PendingSpawn p)
        {
            int classId = EntityClass.FromString(p.EntityClassName);
            if (classId <= 0)
            {
                Log.Warning("[ZombieCompanion] saved thrall class '" + p.EntityClassName
                    + "' no longer exists; dropping the record.");
                ThrallStore.Forget(p.OwnerKey, p.EntityClassName);
                return true;
            }

            // Behind and slightly to the side, keeping the owner's Y. Sampling terrain height here
            // would drop the thrall through the floor of any POI the player logged out inside.
            Vector3 pos = owner.position - owner.transform.forward * 2f + owner.transform.right * 0.5f;
            pos.y = owner.position.y + 0.25f;

            Entity e;
            try
            {
                e = EntityFactory.CreateEntity(classId, pos, new Vector3(0f, owner.rotation.y, 0f));
            }
            catch (Exception ex)
            {
                ThrallRuntime.DebugLog("thrall spawn attempt failed: " + ex.Message);
                return false;
            }

            var alive = e as EntityAlive;
            if (alive == null) return false;

            // Set before the entity enters the world: StaticSpawner keeps the biome and blood-moon
            // spawn managers from counting this zombie against their budget, or culling it.
            alive.SetSpawnerSource(EnumSpawnerSource.StaticSpawner);

            world.SpawnEntityInWorld(alive);

            ThrallRuntime.MakeThrall(alive, owner, p.OwnerKey);

            if (ZcConfig.NotifyProgress)
            {
                GameManager.ShowTooltipMP(owner,
                    ThrallRuntime.FriendlyName(alive) + " drags itself back to your side.", null);
            }

            Log.Out("[ZombieCompanion] respawned " + p.EntityClassName + " for " + owner.EntityName
                + " (entity " + alive.entityId + ")");
            return true;
        }
    }
}
