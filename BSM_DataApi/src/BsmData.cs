using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

/// <summary>
/// Breakneck Server Manager data command.
///   bsmdata            -> {claims:[...],bedrolls:[...]}    (map overlays)
///   bsmdata stats      -> {players:[{deep per-player save stats}]}  (portal Tier-4 stats)
/// Reflection-based so it survives game-version member changes. Offline-safe: reads each
/// player's .ttp save via PlayerDataFile.
/// </summary>
public class BsmData : ConsoleCmdAbstract
{
    public override string[] getCommands() { return new string[] { "bsmdata", "bsm" }; }
    public override string getDescription() { return "Breakneck Server Manager: land claims + bedrolls, or deep player stats as JSON."; }
    public override string getHelp() { return "bsmdata            -> claims+bedrolls JSON\nbsmdata stats      -> per-player deep stats JSON"; }

    private const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    internal static object F(object o, string name)
    {
        if (o == null) return null;
        try { FieldInfo fi = o.GetType().GetField(name, BF); if (fi != null) return fi.GetValue(o); } catch { }
        try { PropertyInfo pi = o.GetType().GetProperty(name, BF); if (pi != null) return pi.GetValue(o, null); } catch { }
        return null;
    }
    private static long Ni(object o, string n) { object v = F(o, n); try { return v == null ? 0 : Convert.ToInt64(v); } catch { return 0; } }
    private static double Nf(object o, string n) { object v = F(o, n); try { return v == null ? 0 : Convert.ToDouble(v); } catch { return 0; } }
    private static bool Nb(object o, string n) { object v = F(o, n); return v is bool && (bool)v; }

    /*
     * Get the item array out of whatever a PlayerDataFile hands us.
     *
     * THIS IS THE BUG THAT MADE EVERY INVENTORY READ RETURN ZERO. Verified against
     * Assembly-CSharp for V3.0.1:
     *
     *   PlayerDataFile.inventory  ItemStack[]   <- toolbelt, already an array
     *   PlayerDataFile.bag        Bag           <- NOT an array; the ItemStack[] is Bag.items
     *   PlayerDataFile.equipment  Equipment     <- NOT an array; ItemValue[] is Equipment.m_slots
     *
     * The old code did `arr as IEnumerable` directly, so bag and equipment both cast to null
     * and silently counted nothing - for 25 players with tens of thousands of crafted items.
     */
    private static IEnumerable Unwrap(object o)
    {
        if (o == null) return null;
        IEnumerable direct = o as IEnumerable;
        if (direct != null) return direct;
        object inner = F(o, "items");                    // Bag
        if (inner == null) inner = F(o, "m_slots");      // Equipment
        return inner as IEnumerable;
    }

    // Count occupied slots. Handles ItemStack (has a count) and bare ItemValue (one item).
    private static int CountStacks(object arr)
    {
        IEnumerable e = Unwrap(arr);
        if (e == null) return 0;
        int n = 0;
        foreach (object it in e)
        {
            if (it == null) continue;
            object iv = F(it, "itemValue");
            if (iv == null) iv = it;                     // Equipment slot: the ItemValue itself
            int type = 0;
            try { object t = F(iv, "type"); if (t != null) type = Convert.ToInt32(t); } catch { }
            if (type <= 0) continue;                     // empty slot
            object c = F(it, "count");
            if (c == null) { n++; continue; }            // no count field = one equipped item
            try { if (Convert.ToInt64(c) > 0) n++; } catch { }
        }
        return n;
    }

    private static string GetName(object p, object key)
    {
        string[] cand = { "PlayerName", "EntityName", "Name" };
        foreach (string m in cand)
        {
            object v = F(p, m);
            if (v is string && ((string)v).Length > 0) return (string)v;
        }
        return key != null ? key.ToString() : "";
    }

    internal static string Esc(string s)
    {
        if (s == null) return "";
        StringBuilder b = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') { b.Append('\\'); b.Append(c); }
            else if (c < 32) { b.Append(' '); }
            else b.Append(c);
        }
        return b.ToString();
    }

    private static string Fnum(double d) { return d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); }

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        string mode = (_params != null && _params.Count > 0) ? _params[0].ToLower() : "";
        if (mode == "stats") { EmitStats(); return; }
        if (mode == "inv") { EmitInv((_params != null && _params.Count > 1) ? _params[1] : ""); return; }
        if (mode == "load") { EmitLoad(); return; }
        EmitOverlays();
    }

    // bsmdata inv <eosid> -> the player's backpack + equipped items (offline via .ttp)
    private void AppendItems(StringBuilder sb, object arr, string slot, ref bool first, ref int n)
    {
        IEnumerable e = Unwrap(arr);       // see Unwrap: bag/equipment are objects, not arrays
        if (e == null) return;
        foreach (object st in e)
        {
            if (st == null) continue;
            // An ItemStack carries {itemValue, count}. An Equipment slot IS an ItemValue,
            // with no count - one worn item per slot.
            object iv = F(st, "itemValue");
            long cnt;
            if (iv != null)
            {
                cnt = 0; object cO = F(st, "count");
                try { cnt = cO == null ? 0 : Convert.ToInt64(cO); } catch { }
            }
            else { iv = st; cnt = 1; }
            if (iv == null || cnt <= 0) continue;

            int type = 0; try { object t = F(iv, "type"); if (t != null) type = Convert.ToInt32(t); } catch { }
            if (type <= 0) continue;       // empty slot

            string name = "";
            try { ItemClass ic = ItemClass.GetForId(type); if (ic != null) name = ic.GetItemName(); } catch { }
            if (name == null || name.Length == 0) name = "item_" + type;

            // Quality is what an admin actually wants to see on gear and tools.
            int qual = 0;
            try { object q = F(iv, "Quality"); if (q != null) qual = Convert.ToInt32(q); } catch { }

            if (!first) sb.Append(","); first = false; n++;
            sb.Append("{\"item\":\"").Append(Esc(name))
              .Append("\",\"count\":").Append(cnt)
              .Append(",\"quality\":").Append(qual)
              .Append(",\"slot\":\"").Append(slot).Append("\"}");
        }
    }
    private void EmitInv(string pid)
    {
        if (pid == null || pid.Length == 0) { SdtdConsole.Instance.Output("{\"error\":\"usage: bsmdata inv <playerid>\",\"items\":[]}"); return; }
        StringBuilder items = new StringBuilder(); bool first = true; int n = 0;
        try
        {
            PlayerDataFile pdf = new PlayerDataFile();
            pdf.Load(GameIO.GetPlayerDataDir(), pid);
            if (!Nb(pdf, "bLoaded")) { SdtdConsole.Instance.Output("{\"error\":\"player not found\",\"items\":[]}"); return; }
            // The toolbelt was missed entirely before - it's a separate ItemStack[] and it
            // is where most of what a player is actually carrying lives.
            AppendItems(items, F(pdf, "inventory"), "belt", ref first, ref n);
            AppendItems(items, F(pdf, "bag"), "bag", ref first, ref n);
            AppendItems(items, F(pdf, "equipment"), "gear", ref first, ref n);
        }
        catch (Exception e) { SdtdConsole.Instance.Output("{\"error\":\"" + Esc(e.Message) + "\",\"items\":[]}"); return; }
        SdtdConsole.Instance.Output("{\"itemCount\":" + n + ",\"items\":[" + items + "]}");
    }

    private void EmitStats()
    {
        StringBuilder players = new StringBuilder();
        bool first = true; int n = 0;
        try
        {
            string dir = GameIO.GetPlayerDataDir();
            PersistentPlayerList ppl = GameManager.Instance.GetPersistentPlayerList();
            if (ppl != null && ppl.Players != null)
            {
                foreach (var kv in ppl.Players)
                {
                    PersistentPlayerData p = kv.Value;
                    if (p == null) continue;
                    string name = GetName(p, kv.Key);
                    string pid = p.PrimaryId != null ? p.PrimaryId.ToString() : (kv.Key != null ? kv.Key.ToString() : "");
                    if (pid.Length == 0) continue;

                    PlayerDataFile pdf = new PlayerDataFile();
                    try { pdf.Load(dir, pid); } catch { continue; }
                    if (!Nb(pdf, "bLoaded")) continue;

                    int bagItems = CountStacks(F(pdf, "inventory")) + CountStacks(F(pdf, "bag"));
                    int gearItems = CountStacks(F(pdf, "equipment"));

                    if (!first) players.Append(",");
                    first = false; n++;
                    players.Append("{")
                        .Append("\"name\":\"").Append(Esc(name)).Append("\"")
                        .Append(",\"id\":\"").Append(Esc(pid)).Append("\"")
                        .Append(",\"deaths\":").Append(Ni(pdf, "deaths"))
                        .Append(",\"playerKills\":").Append(Ni(pdf, "playerKills"))
                        .Append(",\"zombieKills\":").Append(Ni(pdf, "zombieKills"))
                        .Append(",\"score\":").Append(Ni(pdf, "score"))
                        .Append(",\"itemsCrafted\":").Append(Ni(pdf, "totalItemsCrafted"))
                        .Append(",\"distanceWalked\":").Append(Fnum(Nf(pdf, "distanceWalked")))
                        .Append(",\"timePlayed\":").Append(Fnum(Nf(pdf, "totalTimePlayed")))
                        .Append(",\"longestLife\":").Append(Fnum(Nf(pdf, "longestLife")))
                        .Append(",\"currentLife\":").Append(Fnum(Nf(pdf, "currentLife")))
                        .Append(",\"bagItems\":").Append(bagItems)
                        .Append(",\"gearItems\":").Append(gearItems)
                        .Append(",\"dead\":").Append(Nb(pdf, "bDead") ? "true" : "false")
                        .Append("}");
                }
            }
        }
        catch (Exception e)
        {
            SdtdConsole.Instance.Output("{\"error\":\"" + Esc(e.Message) + "\",\"players\":[]}");
            return;
        }
        SdtdConsole.Instance.Output("{\"playerCount\":" + n + ",\"players\":[" + players + "]}");
    }

    /*
     * bsmdata load -> where the server's load actually is.
     *
     * Counts TILE ENTITIES per loaded chunk and attributes each chunk to the nearest land
     * claim. Tile entities - chests, forges, workstations, sign blocks - are what actually
     * cost a 7DtD server frames; plain terrain blocks are nearly free by comparison, which
     * is why "my base has 20,000 blocks" is the wrong number to chase and this reports the
     * expensive one instead.
     *
     * Everything goes through reflection rather than binding to Chunk/ChunkCache directly:
     * those types move between game versions, and a mod that fails to LOAD is far worse
     * than one feature that reports "unavailable".
     */
    // Populated by EmitLoad so a wrong-looking result can be diagnosed from the panel
    // instead of another build-and-restart cycle.
    private string Diag = "";

    private void EmitLoad()
    {
        StringBuilder sb = new StringBuilder();
        int chunkCount = 0;
        long tileTotal = 0;
        bool first = true;
        string dCache = "?", dArr = "?", dCount = "?", dVia = "none";

        try
        {
            object world = F(GameManager.Instance, "World");
            if (world == null) world = GameManager.Instance.World;
            object cache = F(world, "ChunkCache");
            if (cache == null)
            {
                SdtdConsole.Instance.Output("{\"ok\":false,\"error\":\"chunk cache unavailable on this game build\"}");
                return;
            }

            /*
             * World.ChunkCache is a ChunkCluster, and the accessor is
             * GetChunkArrayCopySync() - verified by decompiling Assembly-CSharp for V3.1.0.
             * The obvious-looking GetChunkArray() does NOT exist on it: calling it returned
             * nothing and this whole report read zero chunks while `mem` said nine.
             * The alternatives are kept as fallbacks in case a future build renames it.
             */
            object arr = null;
            string[] candidates = new string[] { "GetChunkArrayCopySync", "GetChunkArray", "GetChunkArrayCopy" };
            dCache = cache.GetType().Name;
            try
            {
                MethodInfo cm = cache.GetType().GetMethod("Count", BF);
                if (cm != null && cm.GetParameters().Length == 0) dCount = Convert.ToString(cm.Invoke(cache, null));
            }
            catch { }

            foreach (string mname in candidates)
            {
                if (arr != null) break;
                try
                {
                    MethodInfo mi = cache.GetType().GetMethod(mname, BF);
                    if (mi != null && mi.GetParameters().Length == 0) { arr = mi.Invoke(cache, null); dVia = mname; }
                }
                catch { }
            }
            if (arr == null) { arr = F(cache, "chunks"); if (arr != null) dVia = "chunks"; }
            // A dictionary of chunks is just as good, but we want its values.
            if (arr != null && !(arr is IEnumerable)) { arr = F(arr, "Values"); dVia += "+Values"; }
            if (arr != null) dArr = arr.GetType().Name;

            IEnumerable chunks = arr as IEnumerable;
            if (chunks == null)
            {
                SdtdConsole.Instance.Output("{\"ok\":false,\"error\":\"could not enumerate loaded chunks\"}");
                return;
            }

            // Claim positions, so a heavy chunk can be blamed on someone.
            List<Vector3i> claimPos = new List<Vector3i>();
            List<string> claimOwner = new List<string>();
            try
            {
                PersistentPlayerList ppl = GameManager.Instance.GetPersistentPlayerList();
                if (ppl != null && ppl.Players != null)
                {
                    foreach (var kv in ppl.Players)
                    {
                        PersistentPlayerData p = kv.Value;
                        if (p == null || p.LPBlocks == null) continue;
                        string nm = GetName(p, kv.Key);
                        foreach (Vector3i v in p.LPBlocks) { claimPos.Add(v); claimOwner.Add(nm); }
                    }
                }
            }
            catch { }

            // owner -> tile entity count
            Dictionary<string, long> byOwner = new Dictionary<string, long>();

            foreach (object chRaw in chunks)
            {
                if (chRaw == null) continue;
                // Tolerate a dictionary that yields KeyValuePair rather than the chunk itself.
                object ch = chRaw;
                if (ch.GetType().IsGenericType && ch.GetType().Name.StartsWith("KeyValuePair"))
                {
                    object v = F(ch, "Value");
                    if (v != null) ch = v;
                }
                chunkCount++;

                long tiles = 0;
                try
                {
                    MethodInfo gte = ch.GetType().GetMethod("GetTileEntities", BF);
                    object tes = (gte != null) ? gte.Invoke(ch, null) : null;
                    // V3 returns a wrapper whose `dict` (or `list`) holds the entries;
                    // Count/Length covers both without walking the collection.
                    if (tes != null)
                    {
                        object cnt = F(tes, "Count");
                        if (cnt == null) cnt = F(F(tes, "dict"), "Count");
                        if (cnt == null) cnt = F(F(tes, "list"), "Count");
                        if (cnt != null) { try { tiles = Convert.ToInt64(cnt); } catch { } }
                    }
                }
                catch { }
                if (tiles <= 0) continue;
                tileTotal += tiles;

                // Chunk centre in world blocks (chunks are 16x16).
                long cx = Ni(ch, "X") * 16 + 8;
                long cz = Ni(ch, "Z") * 16 + 8;

                string owner = "(unclaimed)";
                double best = double.MaxValue;
                for (int i = 0; i < claimPos.Count; i++)
                {
                    double dx = claimPos[i].x - cx, dz = claimPos[i].z - cz;
                    double d = dx * dx + dz * dz;
                    // 64 blocks: comfortably wider than a claim, so a base that spills over
                    // its own claim edge still gets attributed to its owner.
                    if (d < best && d <= 64 * 64) { best = d; owner = claimOwner[i]; }
                }

                if (!byOwner.ContainsKey(owner)) byOwner[owner] = 0;
                byOwner[owner] += tiles;
            }

            foreach (KeyValuePair<string, long> kv in byOwner)
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"owner\":\"").Append(Esc(kv.Key)).Append("\",\"tiles\":").Append(kv.Value).Append("}");
            }
        }
        catch (Exception e)
        {
            SdtdConsole.Instance.Output("{\"ok\":false,\"error\":\"" + Esc(e.Message) + "\"}");
            return;
        }

        Diag = ",\"diag\":{\"cache\":\"" + Esc(dCache) + "\",\"via\":\"" + Esc(dVia)
             + "\",\"arr\":\"" + Esc(dArr) + "\",\"cacheCount\":\"" + Esc(dCount) + "\"}";

        SdtdConsole.Instance.Output("{\"ok\":true,\"chunks\":" + chunkCount +
                                    ",\"tiles\":" + tileTotal + ",\"owners\":[" + sb + "]" + Diag + "}");
    }

    private void EmitOverlays()
    {
        StringBuilder claims = new StringBuilder();
        StringBuilder beds = new StringBuilder();
        bool fc = true, fb = true;
        int cc = 0, bc = 0;
        try
        {
            PersistentPlayerList ppl = GameManager.Instance.GetPersistentPlayerList();
            if (ppl != null && ppl.Players != null)
            {
                foreach (var kv in ppl.Players)
                {
                    PersistentPlayerData p = kv.Value;
                    if (p == null) continue;
                    string name = GetName(p, kv.Key);
                    string pid = p.PrimaryId != null ? p.PrimaryId.ToString() : (kv.Key != null ? kv.Key.ToString() : "");

                    List<Vector3i> lp = p.LPBlocks;
                    if (lp != null)
                    {
                        foreach (Vector3i v in lp)
                        {
                            if (!fc) claims.Append(",");
                            fc = false;
                            claims.Append("{\"name\":\"").Append(Esc(name)).Append("\",\"id\":\"").Append(Esc(pid))
                                  .Append("\",\"x\":").Append(v.x).Append(",\"y\":").Append(v.y).Append(",\"z\":").Append(v.z).Append("}");
                            cc++;
                        }
                    }

                    Vector3i b = p.BedrollPos;
                    if (b.y > -100 && b.y < 400 && !(b.x == 0 && b.y == 0 && b.z == 0))
                    {
                        if (!fb) beds.Append(",");
                        fb = false;
                        beds.Append("{\"name\":\"").Append(Esc(name)).Append("\",\"id\":\"").Append(Esc(pid))
                            .Append("\",\"x\":").Append(b.x).Append(",\"y\":").Append(b.y).Append(",\"z\":").Append(b.z).Append("}");
                        bc++;
                    }
                }
            }
        }
        catch (Exception e)
        {
            SdtdConsole.Instance.Output("{\"error\":\"" + Esc(e.Message) + "\",\"claims\":[],\"bedrolls\":[]}");
            return;
        }
        SdtdConsole.Instance.Output("{\"claimCount\":" + cc + ",\"bedCount\":" + bc + ",\"claims\":[" + claims + "],\"bedrolls\":[" + beds + "]}");
    }
}

/// <summary>
/// Breakneck Server Manager weather control.
///   bsmweather rain|storm|snow|clear|reset
/// Sets WeatherManager's static force* override fields, which the weather system honors —
/// so forced weather STICKS (the console `weather` command only sets a value the biome sim
/// immediately reverts). `reset` returns control to the biome simulation (-1 sentinel).
/// </summary>
public class BsmWeather : ConsoleCmdAbstract
{
    public override string[] getCommands() { return new string[] { "bsmweather" }; }
    public override string getDescription() { return "Breakneck Server Manager: force weather that sticks (rain/storm/snow/clear/reset)."; }
    public override string getHelp() { return "bsmweather rain|storm|snow|clear|reset"; }

    private static void SetF(string field, float v)
    {
        try
        {
            FieldInfo f = typeof(WeatherManager).GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (f != null) f.SetValue(null, v);
        }
        catch { }
    }

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        string mode = (_params != null && _params.Count > 0) ? _params[0].ToLower() : "";
        const float OFF = -1f; // sentinel: not forced -> biome sim resumes
        switch (mode)
        {
            case "rain":
                SetF("forceRain", 1f); SetF("forceClouds", 1f); SetF("forceSnowfall", 0f); SetF("forceWind", 0.3f);
                break;
            case "storm":
                SetF("forceRain", 1f); SetF("forceClouds", 1f); SetF("forceWind", 1f); SetF("forceSnowfall", 0f);
                break;
            case "snow":
                SetF("forceSnowfall", 1f); SetF("forceClouds", 1f); SetF("forceRain", 0f); SetF("forceWind", 0.3f);
                break;
            case "clear":
                SetF("forceRain", 0f); SetF("forceSnowfall", 0f); SetF("forceClouds", 0f); SetF("forceWind", 0f);
                break;
            case "reset":
            case "off":
                SetF("forceRain", OFF); SetF("forceSnowfall", OFF); SetF("forceClouds", OFF); SetF("forceWind", OFF); SetF("forceTemperature", OFF);
                break;
            default:
                SdtdConsole.Instance.Output("{\"ok\":false,\"error\":\"usage: bsmweather rain|storm|snow|clear|reset\"}");
                return;
        }
        SdtdConsole.Instance.Output("{\"ok\":true,\"weather\":\"" + BsmData.Esc(mode) + "\"}");
    }
}

/// <summary>
/// PvP / PvE zone store + rules. Zones are circles (x,z,radius) tagged safe (PvE) or hostile (PvP).
/// defaultSafe sets the world outside all zones. cooldownMs: after taking/dealing PvP damage while
/// in hostile territory, a player stays vulnerable for this long even inside a safe zone (no fleeing
/// mid-fight into safety / combat-logging). All thread-safe-enough for single-writer console pushes.
/// </summary>
public static class BsmZones
{
    public class Zone { public double x, z, r; public bool safe; }
    public static readonly List<Zone> Zones = new List<Zone>();
    public static bool DefaultSafe = false;      // false = PvP world w/ safe zones
    public static long CooldownMs = 60000;       // 60s combat cooldown
    public static bool Enabled = false;          // only enforce once zones are pushed
    private static readonly Dictionary<int, long> LastCombat = new Dictionary<int, long>();

    public static long NowMs() { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond; }

    public static bool SafeAt(object entity)
    {
        try
        {
            object pos = BsmData.F(entity, "position");
            if (pos == null) return DefaultSafe;
            double x = Convert.ToDouble(BsmData.F(pos, "x"));
            double z = Convert.ToDouble(BsmData.F(pos, "z"));
            bool safe = DefaultSafe;
            for (int i = 0; i < Zones.Count; i++)
            {
                Zone zn = Zones[i];
                double dx = x - zn.x, dz = z - zn.z;
                if (dx * dx + dz * dz <= zn.r * zn.r) safe = zn.safe; // later zone wins
            }
            return safe;
        }
        catch { return DefaultSafe; }
    }

    public static void StampCombat(int entityId, long now) { LastCombat[entityId] = now; }

    public static bool Protected(int entityId, bool inSafe, long now)
    {
        if (!inSafe) return false;
        long last; if (!LastCombat.TryGetValue(entityId, out last)) return true;
        return (now - last) >= CooldownMs;
    }
}

/// <summary>
/// Harmony prefix that cancels player-vs-player damage when the victim is protected (in a safe zone
/// and past the combat cooldown). Fail-safe: any uncertainty → allow normal damage. Zombie/animal
/// damage (victim not a player, or attacker not a player) is never touched.
/// </summary>
[HarmonyLib.HarmonyPatch(typeof(EntityAlive), "DamageEntity")]
public static class BsmPvpPatch
{
    private static EntityPlayer ResolvePlayer(object ctx, int id)
    {
        try
        {
            object w = BsmData.F(ctx, "world");
            if (w == null) return null;
            System.Reflection.MethodInfo m = w.GetType().GetMethod("GetEntity", new Type[] { typeof(int) });
            if (m == null) return null;
            return m.Invoke(w, new object[] { id }) as EntityPlayer;
        }
        catch { return null; }
    }

    static bool Prefix(EntityAlive __instance, DamageSource _damageSource, ref int __result)
    {
        try
        {
            if (!BsmZones.Enabled) return true;
            EntityPlayer victim = __instance as EntityPlayer;
            if (victim == null || _damageSource == null) return true; // not a player victim → normal
            int aid = _damageSource.getEntityId();
            if (aid < 0) return true;
            EntityPlayer attacker = ResolvePlayer(__instance, aid);
            if (attacker == null || attacker == victim) return true;   // not PvP → normal

            long now = BsmZones.NowMs();
            bool vSafe = BsmZones.SafeAt(victim);
            bool aSafe = BsmZones.SafeAt(attacker);
            int vId = Convert.ToInt32(BsmData.F(victim, "entityId"));
            int aId2 = Convert.ToInt32(BsmData.F(attacker, "entityId"));
            if (!vSafe) BsmZones.StampCombat(vId, now);   // fighting in hostile territory
            if (!aSafe) BsmZones.StampCombat(aId2, now);

            if (BsmZones.Protected(vId, vSafe, now)) { __result = 0; return false; } // block PvP
        }
        catch { }
        return true; // fail-safe
    }
}

/// <summary>Loads the Harmony patch on server start.</summary>
public class BsmModApi : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        try
        {
            HarmonyLib.Harmony h = new HarmonyLib.Harmony("com.breakneck.bsm");
            h.PatchAll(System.Reflection.Assembly.GetExecutingAssembly());
            UnityEngine.Debug.Log("[BSM] PvP/PvE zone patch loaded.");
        }
        catch (Exception e) { UnityEngine.Debug.LogError("[BSM] patch load failed: " + e.Message); }
    }
}

/// <summary>
/// Console command to push zone config from the panel:
///   bsmzones set &lt;defaultSafe 0|1&gt; &lt;cooldownSec&gt; &lt;x,z,r,safe;x,z,r,safe;...&gt;
///   bsmzones clear         (disable enforcement)
///   bsmzones show          (dump current zones as JSON)
/// </summary>
public class BsmZonesCmd : ConsoleCmdAbstract
{
    public override string[] getCommands() { return new string[] { "bsmzones" }; }
    public override string getDescription() { return "Breakneck Server Manager: set PvP/PvE zones + combat cooldown."; }
    public override string getHelp() { return "bsmzones set <defaultSafe 0|1> <cooldownSec> <x,z,r,safe;...>  |  bsmzones clear  |  bsmzones show"; }

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        string mode = (_params != null && _params.Count > 0) ? _params[0].ToLower() : "";
        try
        {
            if (mode == "clear")
            {
                BsmZones.Zones.Clear(); BsmZones.Enabled = false;
                SdtdConsole.Instance.Output("{\"ok\":true,\"enabled\":false}");
                return;
            }
            if (mode == "show")
            {
                StringBuilder b = new StringBuilder();
                b.Append("{\"enabled\":").Append(BsmZones.Enabled ? "true" : "false")
                 .Append(",\"defaultSafe\":").Append(BsmZones.DefaultSafe ? "true" : "false")
                 .Append(",\"cooldownMs\":").Append(BsmZones.CooldownMs).Append(",\"zones\":[");
                for (int i = 0; i < BsmZones.Zones.Count; i++)
                {
                    BsmZones.Zone z = BsmZones.Zones[i];
                    if (i > 0) b.Append(",");
                    b.Append("{\"x\":").Append(z.x).Append(",\"z\":").Append(z.z).Append(",\"r\":").Append(z.r).Append(",\"safe\":").Append(z.safe ? "true" : "false").Append("}");
                }
                b.Append("]}");
                SdtdConsole.Instance.Output(b.ToString());
                return;
            }
            if (mode == "set" && _params.Count >= 3)
            {
                BsmZones.DefaultSafe = _params[1] == "1";
                BsmZones.CooldownMs = (long)(double.Parse(_params[2], inv) * 1000);
                BsmZones.Zones.Clear();
                if (_params.Count >= 4 && _params[3].Length > 0)
                {
                    string[] parts = _params[3].Split(';');
                    foreach (string part in parts)
                    {
                        if (part.Length == 0) continue;
                        string[] f = part.Split(',');
                        if (f.Length < 4) continue;
                        BsmZones.Zone z = new BsmZones.Zone();
                        z.x = double.Parse(f[0], inv); z.z = double.Parse(f[1], inv);
                        z.r = double.Parse(f[2], inv); z.safe = f[3] == "1";
                        BsmZones.Zones.Add(z);
                    }
                }
                BsmZones.Enabled = true;
                SdtdConsole.Instance.Output("{\"ok\":true,\"enabled\":true,\"zoneCount\":" + BsmZones.Zones.Count + "}");
                return;
            }
            SdtdConsole.Instance.Output("{\"ok\":false,\"error\":\"usage: bsmzones set <0|1> <cooldownSec> <x,z,r,safe;...> | clear | show\"}");
        }
        catch (Exception e) { SdtdConsole.Instance.Output("{\"ok\":false,\"error\":\"" + BsmData.Esc(e.Message) + "\"}"); }
    }
}

/// <summary>
/// Spawn entities (bosses / hordes / events) with optional custom HP and scale.
///   bsmspawn &lt;entityName&gt; &lt;count&gt; &lt;all|playerName|x,y,z&gt; [hp] [scale]
/// e.g. bsmspawn zombieBoss 1 all 5000 1.5   |   bsmspawn animalZombieBear 5 Heath
/// </summary>
public class BsmSpawn : ConsoleCmdAbstract
{
    public override string[] getCommands() { return new string[] { "bsmspawn" }; }
    public override string getDescription() { return "Breakneck Server Manager: spawn entities (bosses/hordes) with optional HP + scale."; }
    public override string getHelp() { return "bsmspawn <entityName> <count> <all|playerName|x,y,z> [hp] [scale]"; }

    private const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    private static void SetNum(object o, string name, float v)
    {
        if (o == null) return;
        try { System.Reflection.FieldInfo f = o.GetType().GetField(name, BF); if (f != null) { f.SetValue(o, Convert.ChangeType(v, f.FieldType)); return; } } catch { }
        try { System.Reflection.PropertyInfo p = o.GetType().GetProperty(name, BF); if (p != null && p.CanWrite) p.SetValue(o, Convert.ChangeType(v, p.PropertyType), null); } catch { }
    }
    private static void SetHealth(EntityAlive a, float hp)
    {
        object stats = BsmData.F(a, "Stats"); if (stats == null) return;
        object health = BsmData.F(stats, "Health"); if (health == null) return;
        SetNum(health, "BaseMax", hp); SetNum(health, "OriginalMax", hp); SetNum(health, "Max", hp); SetNum(health, "ValueMax", hp); SetNum(health, "Value", hp);
    }

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        if (_params == null || _params.Count < 3) { SdtdConsole.Instance.Output("{\"ok\":false,\"error\":\"usage: bsmspawn <entity> <count> <all|player|x,y,z> [hp] [scale]\"}"); return; }
        try
        {
            string ename = _params[0];
            int count = 1; try { count = Math.Max(1, Math.Min(50, int.Parse(_params[1]))); } catch { }
            string target = _params[2];
            float hp = 0f; if (_params.Count > 3) { try { hp = float.Parse(_params[3], inv); } catch { } }
            float scale = 0f; if (_params.Count > 4) { try { scale = float.Parse(_params[4], inv); } catch { } }

            int cid = EntityClass.FromString(ename);
            if (cid < 0) { SdtdConsole.Instance.Output("{\"ok\":false,\"error\":\"unknown entity '" + BsmData.Esc(ename) + "'\"}"); return; }

            List<UnityEngine.Vector3> spots = new List<UnityEngine.Vector3>();
            if (target.IndexOf(',') >= 0)
            {
                string[] c = target.Split(',');
                if (c.Length >= 3) spots.Add(new UnityEngine.Vector3(float.Parse(c[0], inv), float.Parse(c[1], inv), float.Parse(c[2], inv)));
            }
            else
            {
                List<EntityPlayer> players = GameManager.Instance.World.Players.list;
                if (players != null)
                {
                    bool all = target.ToLower() == "all";
                    foreach (EntityPlayer pl in players)
                    {
                        if (pl == null) continue;
                        if (all || (pl.EntityName != null && pl.EntityName.ToLower() == target.ToLower())) spots.Add(pl.position);
                    }
                }
            }
            if (spots.Count == 0) { SdtdConsole.Instance.Output("{\"ok\":false,\"error\":\"no spawn location (no matching online player?)\"}"); return; }

            int spawned = 0;
            System.Random rnd = new System.Random();
            foreach (UnityEngine.Vector3 baseP in spots)
            {
                for (int i = 0; i < count; i++)
                {
                    UnityEngine.Vector3 p = baseP;
                    p.x += (float)(rnd.NextDouble() * 6 - 3);
                    p.z += (float)(rnd.NextDouble() * 6 - 3);
                    Entity e = EntityFactory.CreateEntity(cid, p);
                    if (e == null) continue;
                    GameManager.Instance.World.SpawnEntityInWorld(e);
                    EntityAlive alive = e as EntityAlive;
                    if (alive != null)
                    {
                        if (hp > 0) SetHealth(alive, hp);
                        if (scale > 0) SetNum(alive, "physicsHeightScale", scale);
                    }
                    spawned++;
                }
            }
            SdtdConsole.Instance.Output("{\"ok\":true,\"spawned\":" + spawned + ",\"entity\":\"" + BsmData.Esc(ename) + "\"}");
        }
        catch (Exception e) { SdtdConsole.Instance.Output("{\"ok\":false,\"error\":\"" + BsmData.Esc(e.Message) + "\"}"); }
    }
}
