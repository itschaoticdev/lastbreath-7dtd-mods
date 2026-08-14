using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ChaoticZombieCompanion
{
    /// <summary>One bound zombie a player owns. Active means "should be standing in the world".</summary>
    public class ThrallRecord
    {
        public string OwnerKey;
        public string EntityClassName;
        public bool Active;
    }

    /// <summary>
    /// Disk persistence for thralls.
    ///
    /// 7DtD does not keep a zombie for you. A dynamically spawned one is not even written to the
    /// save (EntityEnemy.IsSavedToFile), and this mod deliberately keeps thralls out of the save
    /// too, because a thrall restored by the world loader would come back hostile and nameless. So
    /// the mod owns the whole lifecycle: this file remembers the FACT of the thrall, and
    /// ThrallRespawner re-creates it.
    ///
    /// A player may own several (the roster) and have up to MaxActiveThralls of them out at once,
    /// so unlike Beastmaster's single-active kennel this caps the number of Active records rather
    /// than forcing exactly one.
    ///
    /// Deliberately a flat tab-separated file rather than JSON: the game ships no JSON dependency
    /// that is safe to bind against across versions, and a record is two strings and a flag.
    /// </summary>
    public static class ThrallStore
    {
        public const string FileName = "ChaoticZombieCompanionThralls.tsv";

        private static readonly Dictionary<string, List<ThrallRecord>> Owners =
            new Dictionary<string, List<ThrallRecord>>(StringComparer.Ordinal);

        private static bool dirty;
        private static string cachedPath;

        private static string FilePath
        {
            get
            {
                if (cachedPath != null) return cachedPath;
                string dir = GameIO.GetSaveGameDir();
                if (string.IsNullOrEmpty(dir)) return null;
                cachedPath = Path.Combine(dir, FileName);
                return cachedPath;
            }
        }

        public static void Load()
        {
            Owners.Clear();
            dirty = false;
            cachedPath = null;

            string path = FilePath;
            if (path == null || !File.Exists(path))
            {
                Log.Out("[ZombieCompanion] no saved thralls.");
                return;
            }

            try
            {
                int n = 0;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;

                    string[] parts = line.Split('\t');
                    if (parts.Length < 2) continue;

                    string ownerKey = parts[0].Trim();
                    string cls = parts[1].Trim();
                    if (ownerKey.Length == 0 || cls.Length == 0) continue;

                    bool active = parts.Length < 3 || parts[2].Trim() == "1";

                    List<ThrallRecord> list;
                    if (!Owners.TryGetValue(ownerKey, out list))
                    {
                        list = new List<ThrallRecord>();
                        Owners[ownerKey] = list;
                    }
                    list.Add(new ThrallRecord { OwnerKey = ownerKey, EntityClassName = cls, Active = active });
                    n++;
                }

                // Repair anything saved under a larger MaxActiveThralls than the server now runs.
                foreach (var kv in Owners) EnforceActiveCap(kv.Value);

                Log.Out("[ZombieCompanion] loaded " + n + " thrall(s) across " + Owners.Count + " owner(s).");
            }
            catch (Exception e)
            {
                Log.Error("[ZombieCompanion] could not read " + FileName + ": " + e.Message);
            }
        }

        private static void EnforceActiveCap(List<ThrallRecord> list)
        {
            int active = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].Active) continue;
                if (active >= ZcConfig.MaxActiveThralls) list[i].Active = false;
                else active++;
            }
        }

        public static List<ThrallRecord> All(string ownerKey)
        {
            List<ThrallRecord> list;
            if (string.IsNullOrEmpty(ownerKey) || !Owners.TryGetValue(ownerKey, out list))
                return new List<ThrallRecord>();
            return list;
        }

        public static List<ThrallRecord> Actives(string ownerKey)
        {
            var outList = new List<ThrallRecord>();
            foreach (var r in All(ownerKey)) if (r.Active) outList.Add(r);
            return outList;
        }

        public static int Count(string ownerKey) { return All(ownerKey).Count; }
        public static int ActiveCount(string ownerKey) { return Actives(ownerKey).Count; }

        /// <summary>Adds a newly bound zombie. Returns false if the roster is full.</summary>
        public static bool Add(string ownerKey, string entityClassName, bool makeActive)
        {
            if (string.IsNullOrEmpty(ownerKey) || string.IsNullOrEmpty(entityClassName)) return false;

            List<ThrallRecord> list;
            if (!Owners.TryGetValue(ownerKey, out list))
            {
                list = new List<ThrallRecord>();
                Owners[ownerKey] = list;
            }
            if (list.Count >= ZcConfig.MaxOwnedThralls) return false;

            list.Add(new ThrallRecord { OwnerKey = ownerKey, EntityClassName = entityClassName, Active = makeActive });
            if (makeActive) EnforceActiveCap(list);

            dirty = true;
            Flush();
            return true;
        }

        /// <summary>Marks one roster slot out or away. Refuses to exceed MaxActiveThralls.</summary>
        public static bool SetActive(string ownerKey, int index, bool active)
        {
            var list = All(ownerKey);
            if (index < 0 || index >= list.Count) return false;
            if (list[index].Active == active) return true;
            if (active && ActiveCount(ownerKey) >= ZcConfig.MaxActiveThralls) return false;

            list[index].Active = active;
            dirty = true;
            Flush();
            return true;
        }

        /// <summary>
        /// Marks the first record of this class out or away. Used where the caller only knows what
        /// a live thrall IS, not which roster row it came from - two thralls of the same class are
        /// interchangeable as far as the store is concerned.
        /// </summary>
        public static bool SetActiveByClass(string ownerKey, string entityClassName, bool active)
        {
            var list = All(ownerKey);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].EntityClassName == entityClassName && list[i].Active != active)
                    return SetActive(ownerKey, i, active);
            }
            return false;
        }

        /// <summary>Puts everything away - nobody is out.</summary>
        public static void StowAll(string ownerKey)
        {
            var list = All(ownerKey);
            bool changed = false;
            foreach (var r in list) { if (r.Active) { r.Active = false; changed = true; } }
            if (changed) { dirty = true; Flush(); }
        }

        public static ThrallRecord RemoveAt(string ownerKey, int index)
        {
            var list = All(ownerKey);
            if (index < 0 || index >= list.Count) return null;
            ThrallRecord gone = list[index];
            list.RemoveAt(index);
            if (list.Count == 0) Owners.Remove(ownerKey);
            dirty = true;
            Flush();
            return gone;
        }

        /// <summary>
        /// Deletes one record of this class, preferring an active one - i.e. the one that just died.
        /// </summary>
        public static void Forget(string ownerKey, string entityClassName)
        {
            var list = All(ownerKey);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Active && list[i].EntityClassName == entityClassName)
                {
                    RemoveAt(ownerKey, i);
                    return;
                }
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].EntityClassName == entityClassName)
                {
                    RemoveAt(ownerKey, i);
                    return;
                }
            }
        }

        public static void Flush()
        {
            if (!dirty) return;

            string path = FilePath;
            if (path == null) return;

            try
            {
                var sb = new StringBuilder();
                sb.Append("# ChaoticZombieCompanion thralls. ownerId<TAB>entityClass<TAB>active\n");
                foreach (var kv in Owners)
                {
                    foreach (var r in kv.Value)
                    {
                        sb.Append(kv.Key).Append('\t')
                          .Append(r.EntityClassName).Append('\t')
                          .Append(r.Active ? '1' : '0').Append('\n');
                    }
                }

                // Write beside the target then swap, so a crash mid-write cannot leave a
                // half-written file where the real one used to be.
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);

                dirty = false;
            }
            catch (Exception e)
            {
                Log.Error("[ZombieCompanion] could not write " + FileName + ": " + e.Message);
            }
        }
    }
}
