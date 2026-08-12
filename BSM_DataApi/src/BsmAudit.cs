using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

/// <summary>
/// Breakneck Server Manager — container / block audit.
///
/// 7DtD writes nothing to the console log when a player opens a storage box or
/// destroys one, so "my chest disappeared" was previously unanswerable. These
/// patches emit one line per event with a distinctive prefix; the panel's
/// existing log tailer parses them, so no extra network path or credentials are
/// needed inside the game process.
///
/// Line format (single line, stable, easy to regex):
///   [BSM-AUDIT] kind='container_open' player='Kam' pid='EOS_0002abc' pos='123,64,-987' extra='...'
///
/// Every patch is wrapped in try/catch and resolves game members defensively:
/// a signature change between game builds must degrade to "no audit line", never
/// to a crashed server.
/// </summary>
internal static class BsmAudit
{
    internal const string PREFIX = "[BSM-AUDIT]";

    /// <summary>Escape single quotes so the parser's quoted fields stay intact.</summary>
    private static string Q(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace('\'', '`').Replace('\r', ' ').Replace('\n', ' ');
    }

    internal static void Emit(string kind, string player, string pid, string pos, string extra)
    {
        try
        {
            UnityEngine.Debug.Log(PREFIX
                + " kind='" + Q(kind) + "'"
                + " player='" + Q(player) + "'"
                + " pid='" + Q(pid) + "'"
                + " pos='" + Q(pos) + "'"
                + " extra='" + Q(extra) + "'");
        }
        catch { /* never let auditing throw into game code */ }
    }

    /// <summary>Entity id -> display name, best effort.</summary>
    internal static string PlayerName(int entityId)
    {
        try
        {
            World w = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (w == null) return "";
            Entity e = w.GetEntity(entityId);
            EntityAlive a = e as EntityAlive;   // EntityName lives on EntityAlive, not Entity
            return a != null ? (a.EntityName ?? "") : "";
        }
        catch { return ""; }
    }

    /// <summary>Entity id -> platform id (EOS_/Steam_), matching sm_name_cache keys.</summary>
    internal static string PlayerPid(int entityId)
    {
        try
        {
            if (ConnectionManager.Instance == null || ConnectionManager.Instance.Clients == null) return "";
            ClientInfo ci = ConnectionManager.Instance.Clients.ForEntityId(entityId);
            if (ci == null) return "";
            if (ci.CrossplatformId != null) return ci.CrossplatformId.CombinedString;
            if (ci.PlatformId != null) return ci.PlatformId.CombinedString;
            return "";
        }
        catch { return ""; }
    }

    /// <summary>PlatformUserIdentifierAbs -> string id, tolerating nulls.</summary>
    internal static string PidString(object persistentPlayerId)
    {
        try
        {
            if (persistentPlayerId == null) return "";
            PropertyInfo pi = persistentPlayerId.GetType().GetProperty("CombinedString");
            if (pi != null)
            {
                object v = pi.GetValue(persistentPlayerId, null);
                if (v != null) return v.ToString();
            }
            return persistentPlayerId.ToString();
        }
        catch { return ""; }
    }

    /// <summary>Resolve a persistent player id to a name via the persistent player list.</summary>
    internal static string NameForPersistent(object persistentPlayerId)
    {
        try
        {
            if (persistentPlayerId == null) return "";
            PersistentPlayerList ppl = GameManager.Instance != null ? GameManager.Instance.GetPersistentPlayerList() : null;
            if (ppl == null) return "";
            PersistentPlayerData d = ppl.GetPlayerData(persistentPlayerId as PlatformUserIdentifierAbs);
            if (d == null) return "";
            // PlayerName is a PersistentPlayerName struct in V3, not a string.
            object pn = d.PlayerName;
            return pn != null ? pn.ToString() : "";
        }
        catch { return ""; }
    }

    internal static string PosStr(Vector3i p)
    {
        return p.x + "," + p.y + "," + p.z;
    }
}

/// <summary>
/// Container opened. TEFeatureStorage.OnLockedServer is the server-side hook that
/// fires when a player successfully takes the lock on a storage tile entity —
/// i.e. actually opens the box. (OnBlockActivated takes an EntityPlayerLocal and
/// therefore never runs on a dedicated server.)
/// </summary>
[HarmonyPatch(typeof(TEFeatureStorage), "OnLockedServer")]
internal static class BsmPatch_StorageOpened
{
    static void Postfix(TEFeatureStorage __instance, bool _success, int _lockingPlayerID)
    {
        try
        {
            if (!_success) return;                      // failed/contended lock isn't an access
            if (__instance == null) return;
            if (GameManager.Instance == null || !GameManager.IsDedicatedServer) return;

            string pos = "";
            try { pos = BsmAudit.PosStr(__instance.ToWorldPos()); } catch { }

            // Only player storage is interesting; loot crates generate huge volume.
            string extra = "";
            try { extra = __instance.bPlayerStorage ? "player_storage" : "loot"; } catch { }

            BsmAudit.Emit("container_open",
                BsmAudit.PlayerName(_lockingPlayerID),
                BsmAudit.PlayerPid(_lockingPlayerID),
                pos, extra);
        }
        catch { }
    }
}

/// <summary>
/// Block picked up (chest carried away). This is the single most direct answer to
/// "my storage box disappeared" — it names who removed it and from where.
/// </summary>
[HarmonyPatch(typeof(GameManager), "PickupBlockServer")]
internal static class BsmPatch_PickupBlock
{
    static void Prefix(Vector3i _blockPos, BlockValue _blockValue, int _playerId, PlatformUserIdentifierAbs persistentPlayerId)
    {
        try
        {
            if (GameManager.Instance == null || !GameManager.IsDedicatedServer) return;

            string blockName = "";
            try { blockName = _blockValue.Block != null ? _blockValue.Block.GetBlockName() : ""; } catch { }

            string name = BsmAudit.PlayerName(_playerId);
            if (string.IsNullOrEmpty(name)) name = BsmAudit.NameForPersistent(persistentPlayerId);
            string pid = BsmAudit.PlayerPid(_playerId);
            if (string.IsNullOrEmpty(pid)) pid = BsmAudit.PidString(persistentPlayerId);

            BsmAudit.Emit("block_pickup", name, pid, BsmAudit.PosStr(_blockPos), blockName);
        }
        catch { }
    }
}

/// <summary>
/// Block changed (destroyed / replaced). Logging every block change on a busy
/// server would be unusable, so this only reports positions that currently hold a
/// storage tile entity — i.e. someone is about to destroy a container.
/// </summary>
[HarmonyPatch(typeof(GameManager), "ChangeBlocks")]
internal static class BsmPatch_ChangeBlocks
{
    // BlockChangeInfo's position field has moved between builds; resolve once.
    private static FieldInfo _posField;
    private static bool _posResolved;

    private static bool TryGetPos(object bci, out Vector3i pos)
    {
        pos = default(Vector3i);
        try
        {
            if (!_posResolved)
            {
                _posResolved = true;
                Type t = bci.GetType();
                string[] candidates = { "pos", "Pos", "blockPos", "position" };
                foreach (string c in candidates)
                {
                    FieldInfo f = t.GetField(c, BindingFlags.Public | BindingFlags.Instance);
                    if (f != null && f.FieldType == typeof(Vector3i)) { _posField = f; break; }
                }
                if (_posField == null)
                {
                    // fall back to the first Vector3i field on the type
                    foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                        if (f.FieldType == typeof(Vector3i)) { _posField = f; break; }
                }
            }
            if (_posField == null) return false;
            pos = (Vector3i)_posField.GetValue(bci);
            return true;
        }
        catch { return false; }
    }

    static void Prefix(PlatformUserIdentifierAbs persistentPlayerId, List<BlockChangeInfo> _blocksToChange)
    {
        try
        {
            if (_blocksToChange == null || _blocksToChange.Count == 0) return;
            if (GameManager.Instance == null || !GameManager.IsDedicatedServer) return;
            World w = GameManager.Instance.World;
            if (w == null) return;

            // A single change batch can be large (explosions); cap the work done.
            int budget = 24;

            foreach (BlockChangeInfo bci in _blocksToChange)
            {
                if (budget-- <= 0) break;
                Vector3i pos;
                if (!TryGetPos(bci, out pos)) continue;

                // Is there a storage container here right now? (Prefix runs before
                // the change, so the tile entity still exists.)
                TileEntityComposite te = null;
                try { te = w.GetTileEntity(pos) as TileEntityComposite; } catch { continue; }
                if (te == null) continue;

                TEFeatureStorage store = null;
                try { store = te.GetFeature<TEFeatureStorage>(); } catch { }
                if (store == null) continue;

                bool playerStorage = false;
                try { playerStorage = store.bPlayerStorage; } catch { }
                if (!playerStorage) continue;   // ignore world loot crates

                string name = BsmAudit.NameForPersistent(persistentPlayerId);
                string pid = BsmAudit.PidString(persistentPlayerId);
                BsmAudit.Emit("container_destroyed", name, pid, BsmAudit.PosStr(pos), "storage removed/changed");
            }
        }
        catch { }
    }
}
