using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ChaoticBeastmaster
{
    /// <summary>One tamed animal a player owns. Exactly one per owner may be Active (out).</summary>
    public class PetRecord
    {
        public string OwnerKey;
        public string EntityClassName;
        /// <summary>True for the single companion currently allowed in the world.</summary>
        public bool Active;
    }

    /// <summary>
    /// Disk persistence for companions.
    ///
    /// 7DtD does not save animals - a wild wolf exists only while its chunk is loaded, and nothing
    /// survives a restart. So a tamed animal cannot simply be left in the world and expected to
    /// still be there tomorrow; the mod has to remember it and put it back.
    ///
    /// A player may own several (the kennel) but only ever have one out at a time, so each owner's
    /// list carries at most one Active record - that is the one PetRespawner spawns.
    ///
    /// Deliberately a flat tab-separated file rather than JSON: the game ships no JSON dependency
    /// that is safe to bind against across versions, and a record is two strings and a flag.
    /// </summary>
    public static class PetStore
    {
        public const string FileName = "ChaoticBeastmasterPets.tsv";

        private static readonly Dictionary<string, List<PetRecord>> Owners =
            new Dictionary<string, List<PetRecord>>(StringComparer.Ordinal);

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
                Log.Out("[Beastmaster] no saved companions.");
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

                    // Third column is new; files written by 1.0.0 have only two, and those pets
                    // were by definition the player's single out-companion.
                    bool active = parts.Length < 3 || parts[2].Trim() == "1";

                    List<PetRecord> list;
                    if (!Owners.TryGetValue(ownerKey, out list))
                    {
                        list = new List<PetRecord>();
                        Owners[ownerKey] = list;
                    }
                    list.Add(new PetRecord { OwnerKey = ownerKey, EntityClassName = cls, Active = active });
                    n++;
                }

                // Repair anything that somehow ended up with two out at once.
                foreach (var kv in Owners) EnforceSingleActive(kv.Value);

                Log.Out("[Beastmaster] loaded " + n + " companion(s) across " + Owners.Count + " owner(s).");
            }
            catch (Exception e)
            {
                Log.Error("[Beastmaster] could not read " + FileName + ": " + e.Message);
            }
        }

        private static void EnforceSingleActive(List<PetRecord> list)
        {
            bool seen = false;
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].Active) continue;
                if (seen) list[i].Active = false;
                seen = true;
            }
        }

        public static List<PetRecord> All(string ownerKey)
        {
            List<PetRecord> list;
            if (string.IsNullOrEmpty(ownerKey) || !Owners.TryGetValue(ownerKey, out list))
                return new List<PetRecord>();
            return list;
        }

        public static PetRecord GetActive(string ownerKey)
        {
            foreach (var r in All(ownerKey)) if (r.Active) return r;
            return null;
        }

        public static int Count(string ownerKey) { return All(ownerKey).Count; }

        /// <summary>Adds a newly tamed animal. Returns false if the kennel is full.</summary>
        public static bool Add(string ownerKey, string entityClassName, bool makeActive)
        {
            if (string.IsNullOrEmpty(ownerKey) || string.IsNullOrEmpty(entityClassName)) return false;

            List<PetRecord> list;
            if (!Owners.TryGetValue(ownerKey, out list))
            {
                list = new List<PetRecord>();
                Owners[ownerKey] = list;
            }
            if (list.Count >= BmConfig.MaxOwnedPets) return false;

            if (makeActive) foreach (var r in list) r.Active = false;
            list.Add(new PetRecord { OwnerKey = ownerKey, EntityClassName = entityClassName, Active = makeActive });

            dirty = true;
            Flush();
            return true;
        }

        /// <summary>Marks index as the one that is out, stowing whatever was out before.</summary>
        public static bool SetActive(string ownerKey, int index)
        {
            var list = All(ownerKey);
            if (index < 0 || index >= list.Count) return false;
            foreach (var r in list) r.Active = false;
            list[index].Active = true;
            dirty = true;
            Flush();
            return true;
        }

        /// <summary>Puts everything away - nobody is out.</summary>
        public static void StowAll(string ownerKey)
        {
            var list = All(ownerKey);
            bool changed = false;
            foreach (var r in list) { if (r.Active) { r.Active = false; changed = true; } }
            if (changed) { dirty = true; Flush(); }
        }

        public static PetRecord RemoveAt(string ownerKey, int index)
        {
            var list = All(ownerKey);
            if (index < 0 || index >= list.Count) return null;
            PetRecord gone = list[index];
            list.RemoveAt(index);
            if (list.Count == 0) Owners.Remove(ownerKey);
            dirty = true;
            Flush();
            return gone;
        }

        /// <summary>Removes the record currently out, e.g. because it died.</summary>
        public static void ForgetActive(string ownerKey)
        {
            var list = All(ownerKey);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Active) { RemoveAt(ownerKey, i); return; }
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
                sb.Append("# ChaoticBeastmaster companions. ownerId<TAB>entityClass<TAB>active\n");
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
                Log.Error("[Beastmaster] could not write " + FileName + ": " + e.Message);
            }
        }
    }
}
