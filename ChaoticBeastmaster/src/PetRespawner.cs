using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChaoticBeastmaster
{
    /// <summary>
    /// Puts companions back after a relog or a restart, and takes them away when their owner
    /// leaves.
    ///
    /// This exists because 7DtD does not persist animals at all. A tamed wolf is an ordinary
    /// EntityEnemyAnimal: it dies with its chunk, and it certainly does not survive a server
    /// restart. Left alone, every player would log back in to an empty spot where their companion
    /// used to be. So the mod owns the lifecycle - PetStore remembers the fact of the pet, and
    /// this class re-creates it.
    ///
    /// Despawning on logout is the other half of that deal: an unowned pet standing in the world
    /// would wander into a horde, die, and delete the player's record while they were offline.
    /// </summary>
    public static class PetRespawner
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

        public static void OnPlayerSpawned(int entityId, RespawnType respawnType)
        {
            var world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return;

            var player = world.GetEntity(entityId) as EntityPlayer;
            if (player == null) return;

            string ownerKey = TameRuntime.OwnerKeyOf(player);
            if (string.IsNullOrEmpty(ownerKey)) return;

            // Re-point an already-live pet instead of spawning a second one. Happens on death and
            // on teleport, where the player entity is reused or replaced but the pet never left.
            foreach (var kv in TameRuntime.LivePets)
            {
                if (kv.Value.OwnerKey == ownerKey)
                {
                    kv.Value.OwnerEntityId = entityId;
                    TameRuntime.DebugLog("re-linked live pet " + kv.Key + " to " + player.EntityName);
                    return;
                }
            }

            // Only whatever was out when they left comes back. Kennelled ones stay put until
            // called, which is what keeps it to one companion at a time across a relog.
            PetRecord rec = PetStore.GetActive(ownerKey);
            if (rec == null) return;

            QueueSpawn(player, ownerKey, rec.EntityClassName, 3f);
            TameRuntime.DebugLog("queued companion respawn for " + player.EntityName
                + " (" + rec.EntityClassName + ", respawnType=" + respawnType + ")");
        }

        /// <summary>
        /// Schedules a companion to appear beside its owner. The delay exists because on login the
        /// player's chunks are still streaming in, and an entity spawned into an unloaded chunk
        /// falls through the world.
        /// </summary>
        public static void QueueSpawn(EntityPlayer player, string ownerKey, string entityClassName, float delay)
        {
            if (player == null || string.IsNullOrEmpty(ownerKey) || string.IsNullOrEmpty(entityClassName)) return;

            for (int i = 0; i < Pending.Count; i++)
            {
                if (Pending[i].OwnerKey == ownerKey) return; // already queued
            }

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

            var world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return;

            int despawn = -1;
            foreach (var kv in TameRuntime.LivePets)
            {
                if (kv.Value.OwnerKey == ownerKey) { despawn = kv.Key; break; }
            }
            if (despawn < 0) return;

            TameRuntime.LivePets.Remove(despawn);

            // Despawned, not killed: EnumRemoveEntityReason.Killed would fire EntityKilled, and
            // that handler deletes the saved record - which would mean logging out permanently
            // destroys your companion.
            world.RemoveEntity(despawn, EnumRemoveEntityReason.Despawned);
            TameRuntime.DebugLog("stored companion " + despawn + " for departing owner");
        }

        public static void Tick(World world)
        {
            if (Pending.Count == 0) return;

            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                PendingSpawn p = Pending[i];
                if (Time.time < p.DueAt) continue;

                var owner = world.GetEntity(p.OwnerEntityId) as EntityPlayer;
                if (owner == null || owner.IsDead())
                {
                    // Owner died or dropped between joining and the pet arriving. Leave the saved
                    // record alone - they get their companion on the next spawn instead.
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
                    Log.Warning("[Beastmaster] gave up respawning " + p.EntityClassName
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
                Log.Warning("[Beastmaster] saved companion class '" + p.EntityClassName
                    + "' no longer exists; dropping the record.");
                PetStore.ForgetActive(p.OwnerKey);
                return true;
            }

            // Behind and slightly to the side, keeping the owner's Y. Sampling terrain height here
            // would drop the pet through the floor of any POI the player logged out inside.
            Vector3 pos = owner.position - owner.transform.forward * 2f + owner.transform.right * 0.5f;
            pos.y = owner.position.y + 0.25f;

            Entity e;
            try
            {
                e = EntityFactory.CreateEntity(classId, pos, new Vector3(0f, owner.rotation.y, 0f));
            }
            catch (Exception ex)
            {
                TameRuntime.DebugLog("companion spawn attempt failed: " + ex.Message);
                return false;
            }

            var alive = e as EntityAlive;
            if (alive == null) return false;

            // StaticSpawner keeps the biome/wandering-horde spawn manager from counting this
            // animal against its budget, or culling it as surplus wildlife.
            alive.SetSpawnerSource(EnumSpawnerSource.StaticSpawner);

            world.SpawnEntityInWorld(alive);

            TameRuntime.MakeCompanion(alive, owner, p.OwnerKey);

            if (BmConfig.NotifyProgress)
            {
                GameManager.ShowTooltipMP(owner, TameRuntime.FriendlyName(alive) + " falls in beside you.", null);
            }

            Log.Out("[Beastmaster] respawned " + p.EntityClassName + " for " + owner.EntityName
                + " (entity " + alive.entityId + ")");
            return true;
        }
    }
}
