using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PersistentParties
{
    /// <summary>
    /// Who belongs to which party, keyed by the player's persistent platform id
    /// (e.g. "Steam_76561198...") rather than entityId, which is reassigned every
    /// session and is exactly why vanilla parties cannot survive a relog.
    ///
    /// File format, one group per line - no JSON dependency:
    ///     groupId|lastSeenUnix|uid1,uid2,uid3
    /// </summary>
    internal static class Store
    {
        private const string FileName = "parties.dat";

        private class Group
        {
            public int Id;
            public long LastSeen;
            public readonly List<string> Members = new List<string>();
        }

        private static readonly Dictionary<int, Group> groups = new Dictionary<int, Group>();
        private static readonly Dictionary<string, int> memberIndex =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private static string filePath;
        private static int nextId = 1;
        private static bool dirty;

        public static void Load(string modDir, string worldKey)
        {
            // One file per world/save, so two worlds on one box don't share parties.
            string safe = string.Join("_", worldKey.Split(Path.GetInvalidFileNameChars()));
            filePath = Path.Combine(modDir, safe + "." + FileName);

            groups.Clear();
            memberIndex.Clear();
            nextId = 1;

            if (!File.Exists(filePath))
            {
                Log.Out("[PersistentParties] no saved parties yet (" + filePath + ")");
                return;
            }

            try
            {
                foreach (string raw in File.ReadAllLines(filePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;

                    string[] parts = line.Split('|');
                    if (parts.Length < 3) continue;

                    int id;
                    long seen;
                    if (!int.TryParse(parts[0], out id)) continue;
                    long.TryParse(parts[1], out seen);

                    var g = new Group { Id = id, LastSeen = seen };
                    foreach (string uid in parts[2].Split(','))
                    {
                        string u = uid.Trim();
                        if (u.Length == 0) continue;
                        g.Members.Add(u);
                        memberIndex[u] = id;
                    }

                    if (g.Members.Count < 2) continue;   // a one-person party is not a party
                    groups[id] = g;
                    if (id >= nextId) nextId = id + 1;
                }

                PruneStale();
                Log.Out(string.Format("[PersistentParties] loaded {0} saved parties covering {1} players",
                    groups.Count, memberIndex.Count));
            }
            catch (Exception e)
            {
                Log.Error("[PersistentParties] failed reading " + filePath + ": " + e.Message);
            }
        }

        private static void PruneStale()
        {
            if (Cfg.ForgetAfterDays <= 0) return;
            long cutoff = Now() - (long)Cfg.ForgetAfterDays * 86400L;
            var dead = groups.Where(kv => kv.Value.LastSeen > 0 && kv.Value.LastSeen < cutoff)
                             .Select(kv => kv.Key).ToList();
            foreach (int id in dead)
            {
                foreach (string uid in groups[id].Members) memberIndex.Remove(uid);
                groups.Remove(id);
                dirty = true;
            }
            if (dead.Count > 0)
                Log.Out("[PersistentParties] forgot " + dead.Count + " party/parties inactive for over "
                        + Cfg.ForgetAfterDays + " days");
        }

        public static void Save()
        {
            if (!dirty || filePath == null) return;
            try
            {
                var lines = new List<string>
                {
                    "# PersistentParties saved groups - generated, do not hand-edit while the server runs",
                    "# groupId|lastSeenUnix|memberUid,memberUid,..."
                };
                foreach (var g in groups.Values)
                {
                    if (g.Members.Count < 2) continue;
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}",
                        g.Id, g.LastSeen, string.Join(",", g.Members.ToArray())));
                }
                File.WriteAllLines(filePath, lines.ToArray());
                dirty = false;
            }
            catch (Exception e)
            {
                Log.Error("[PersistentParties] failed writing " + filePath + ": " + e.Message);
            }
        }

        private static long Now()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        /// <summary>Group id this player belongs to, or 0.</summary>
        public static int GroupOf(string uid)
        {
            int id;
            return uid != null && memberIndex.TryGetValue(uid, out id) ? id : 0;
        }

        public static List<string> MembersOf(int groupId)
        {
            Group g;
            return groups.TryGetValue(groupId, out g) ? new List<string>(g.Members) : new List<string>();
        }

        /// <summary>
        /// Record the full membership of a live party. Called after any join so the
        /// saved copy always mirrors what the game currently has.
        /// </summary>
        public static void Remember(List<string> uids)
        {
            uids = uids.Where(u => !string.IsNullOrEmpty(u)).Distinct().ToList();
            if (uids.Count < 2)
            {
                // Dropped below two - the remaining member is no longer in a party.
                foreach (string u in uids) Forget(u, false);
                return;
            }

            // Reuse the existing group id if any of these players already had one,
            // so a party keeps its identity as members come and go.
            int id = 0;
            foreach (string u in uids)
            {
                int existing = GroupOf(u);
                if (existing != 0) { id = existing; break; }
            }
            if (id == 0) id = nextId++;

            Group g;
            if (!groups.TryGetValue(id, out g))
            {
                g = new Group { Id = id };
                groups[id] = g;
            }

            foreach (string old in g.Members) memberIndex.Remove(old);
            g.Members.Clear();
            g.Members.AddRange(uids);
            g.LastSeen = Now();
            foreach (string u in uids) memberIndex[u] = id;

            dirty = true;
            Save();
            if (Cfg.DebugLog)
                Log.Out("[PersistentParties] remembered group " + id + ": " + string.Join(", ", uids.ToArray()));
        }

        /// <summary>Drop a player from their saved party. Used for deliberate leave/kick only.</summary>
        public static void Forget(string uid, bool save = true)
        {
            if (string.IsNullOrEmpty(uid)) return;
            int id = GroupOf(uid);
            if (id == 0) return;

            Group g = groups[id];
            g.Members.Remove(uid);
            memberIndex.Remove(uid);
            g.LastSeen = Now();

            if (g.Members.Count < 2)
            {
                foreach (string u in g.Members) memberIndex.Remove(u);
                groups.Remove(id);
            }

            dirty = true;
            if (save) Save();
            if (Cfg.DebugLog) Log.Out("[PersistentParties] forgot " + uid + " (was group " + id + ")");
        }

        public static void Touch(int groupId)
        {
            Group g;
            if (groups.TryGetValue(groupId, out g)) { g.LastSeen = Now(); dirty = true; }
        }

        public static int GroupCount { get { return groups.Count; } }
    }
}
