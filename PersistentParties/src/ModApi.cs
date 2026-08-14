using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PersistentParties
{
    public class ModApi : IModApi
    {
        internal static string ModDir;

        /// <summary>entityId -> persistent platform id, for the current session only.</summary>
        private static readonly Dictionary<int, string> uidByEntity = new Dictionary<int, string>();

        private struct Pending
        {
            public int EntityId;
            public string Uid;
            public float DueAt;
            public int Attempts;
        }

        private static readonly List<Pending> pending = new List<Pending>();

        public void InitMod(Mod _modInstance)
        {
            // The loader logs "Failed initializing ModAPI instance" with no detail,
            // so catch and print the actual reason ourselves.
            try { Init(_modInstance); }
            catch (Exception e) { Log.Error("[PersistentParties] InitMod failed: " + e); }
        }

        private static void Init(Mod _modInstance)
        {
            // Mod.Path, NOT Assembly.Location: 7DtD loads mod assemblies from a byte[],
            // so Location is empty and GetDirectoryName("") throws before anything runs.
            ModDir = _modInstance != null && !string.IsNullOrEmpty(_modInstance.Path)
                ? _modInstance.Path
                : Path.Combine(Directory.GetCurrentDirectory(), "Mods/PersistentParties");

            Cfg.Load(ModDir);
            if (!Cfg.Enabled)
            {
                Log.Out("[PersistentParties] disabled in config - not hooking anything");
                return;
            }

            try
            {
                new Harmony("com.chaotic.persistentparties").PatchAll(Assembly.GetExecutingAssembly());
            }
            catch (Exception e)
            {
                Log.Error("[PersistentParties] Harmony patching failed: " + e);
                return;
            }

            ModEvents.GameStartDone.RegisterHandler(OnGameStartDone);
            ModEvents.PlayerSpawnedInWorld.RegisterHandler(OnPlayerSpawned);
            ModEvents.PlayerDisconnected.RegisterHandler(OnPlayerDisconnected);
            ModEvents.GameUpdate.RegisterHandler(OnGameUpdate);

            Log.Out("[PersistentParties] loaded");
        }

        private static void OnGameStartDone(ref ModEvents.SGameStartDoneData _d)
        {
            if (!IsServer) return;
            string world = GamePrefs.GetString(EnumGamePrefs.GameName);
            if (string.IsNullOrEmpty(world)) world = "default";
            Store.Load(ModDir, world);
        }

        // ------------------------------------------------------------------ join

        private static void OnPlayerSpawned(ref ModEvents.SPlayerSpawnedInWorldData _d)
        {
            if (!IsServer || _d.ClientInfo == null) return;

            string uid = SafeUid(_d.ClientInfo);
            if (uid == null) return;
            uidByEntity[_d.EntityId] = uid;

            if (Store.GroupOf(uid) == 0)
            {
                if (Cfg.DebugLog) Log.Out("[PersistentParties] " + uid + " has no saved party");
                return;
            }

            // Deliberately queued rather than done inline: the client is still
            // finishing its world load here and can miss the party update packet.
            pending.Add(new Pending
            {
                EntityId = _d.EntityId,
                Uid = uid,
                DueAt = Time.time + Mathf.Max(0f, Cfg.RestoreDelaySeconds)
            });
        }

        private static void OnPlayerDisconnected(ref ModEvents.SPlayerDisconnectedData _d)
        {
            if (_d.ClientInfo != null) uidByEntity.Remove(_d.ClientInfo.entityId);
        }

        private static void OnGameUpdate(ref ModEvents.SGameUpdateData _d)
        {
            if (pending.Count == 0) return;

            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Pending p = pending[i];
                if (Time.time < p.DueAt) continue;

                pending.RemoveAt(i);
                try
                {
                    if (!TryRestore(p) && p.Attempts < 3)
                    {
                        // Player object not ready yet - back off and try again.
                        p.Attempts++;
                        p.DueAt = Time.time + 2f;
                        pending.Add(p);
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[PersistentParties] restore failed for " + p.Uid + ": " + e);
                }
            }
        }

        /// <summary>Returns false if it should be retried (player not spawned in yet).</summary>
        private static bool TryRestore(Pending p)
        {
            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return false;

            EntityPlayer player = world.GetEntity(p.EntityId) as EntityPlayer;
            if (player == null) return false;

            if (player.Party != null)
            {
                if (Cfg.DebugLog) Log.Out("[PersistentParties] " + p.Uid + " already in a party");
                return true;
            }

            int groupId = Store.GroupOf(p.Uid);
            if (groupId == 0) return true;

            // Who else from this saved party is online right now?
            var mateUids = Store.MembersOf(groupId).Where(u => u != p.Uid).ToList();
            var mates = new List<EntityPlayer>();
            foreach (string uid in mateUids)
            {
                EntityPlayer m = OnlinePlayer(world, uid);
                if (m != null) mates.Add(m);
            }

            if (mates.Count == 0)
            {
                // Nobody to rejoin yet. Their party is remembered; whoever logs in
                // second is the one who triggers the re-forming.
                if (Cfg.DebugLog)
                    Log.Out("[PersistentParties] " + p.Uid + " is first of party " + groupId + " online - waiting");
                return true;
            }

            // Prefer a mate who already has a live party, but only if that party is
            // entirely made of this group - otherwise they joined someone else since
            // and merging would drag unrelated players together.
            EntityPlayer anchor = null;
            foreach (EntityPlayer m in mates)
            {
                if (m.Party == null) continue;
                if (PartyIsOnlyGroup(m.Party, groupId)) { anchor = m; break; }
            }
            if (anchor == null) anchor = mates.FirstOrDefault(m => m.Party == null);
            if (anchor == null)
            {
                Log.Out("[PersistentParties] " + p.Uid + " could not rejoin party " + groupId
                        + ": every online member is in a different party");
                return true;
            }

            int before = anchor.Party != null ? anchor.Party.MemberList.Count : 0;

            // Reuse the game's own server-side join. This is what makes the clients
            // update: it ends in ConnectionManager.SendPackage(NetPackagePartyData...).
            // Doing our own AddPlayer would change nothing on anyone's screen.
            Party.ServerHandleAcceptInvite(anchor, player);

            Party joined = player.Party;
            if (joined == null || (anchor.Party != null && anchor.Party.MemberList.Count == before && before >= 8))
            {
                Log.Warning("[PersistentParties] " + p.Uid + " could not be added to party " + groupId
                            + " - it is full (vanilla caps parties at 8)");
                return true;
            }

            Store.Touch(groupId);
            Log.Out("[PersistentParties] restored " + PlayerName(player) + " into saved party " + groupId
                    + " (" + joined.MemberList.Count + " members)");

            if (Cfg.Announce) Announce(joined, PlayerName(player) + " rejoined your party");
            return true;
        }

        private static bool PartyIsOnlyGroup(Party party, int groupId)
        {
            foreach (EntityPlayer m in party.MemberList)
            {
                string uid = UidOf(m.entityId);
                if (uid == null || Store.GroupOf(uid) != groupId) return false;
            }
            return true;
        }

        private static EntityPlayer OnlinePlayer(World world, string uid)
        {
            foreach (var kv in uidByEntity)
            {
                if (kv.Value != uid) continue;
                return world.GetEntity(kv.Key) as EntityPlayer;
            }
            return null;
        }

        private static void Announce(Party party, string message)
        {
            try
            {
                foreach (EntityPlayer m in party.MemberList)
                {
                    ClientInfo ci = ConnectionManager.Instance.Clients.ForEntityId(m.entityId);
                    if (ci == null) continue;
                    ci.SendPackage(NetPackageManager.GetPackage<NetPackageChat>()
                        .Setup(EChatType.Whisper, -1, "[Party] " + message, null, EMessageSender.None,
                               GeneratedTextManager.BbCodeSupportMode.Supported));
                }
            }
            catch (Exception e)
            {
                if (Cfg.DebugLog) Log.Warning("[PersistentParties] announce failed: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- helpers

        internal static bool IsServer
        {
            get
            {
                return SingletonMonoBehaviour<ConnectionManager>.Instance != null
                       && SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer;
            }
        }

        internal static string UidOf(int entityId)
        {
            string uid;
            return uidByEntity.TryGetValue(entityId, out uid) ? uid : null;
        }

        internal static List<string> UidsOf(Party party)
        {
            var list = new List<string>();
            if (party == null) return list;
            foreach (EntityPlayer m in party.MemberList)
            {
                string uid = UidOf(m.entityId);
                if (uid != null) list.Add(uid);
            }
            return list;
        }

        private static string SafeUid(ClientInfo ci)
        {
            try
            {
                return ci.PlatformId != null ? ci.PlatformId.CombinedString : null;
            }
            catch { return null; }
        }

        private static string PlayerName(EntityPlayer p)
        {
            return p != null && !string.IsNullOrEmpty(p.PlayerDisplayName) ? p.PlayerDisplayName : "a player";
        }
    }
}
