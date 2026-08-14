using System;
using System.Collections.Generic;
using System.IO;

namespace OfflineRaidProtection
{
    /// <summary>
    /// Optional, loosely-coupled link to the PersistentParties mod.
    ///
    /// We read its saved parties file rather than referencing its assembly, so this
    /// mod works standalone (party_aware simply does nothing) and neither mod has to
    /// be installed for the other to load.
    ///
    /// File format (one group per line):  groupId|lastSeenUnix|uid1,uid2,uid3
    /// </summary>
    internal static class ClanLink
    {
        private static readonly Dictionary<string, List<string>> matesByUid =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        private static string sourceFile;
        private static DateTime lastRead = DateTime.MinValue;

        public static bool Available { get { return sourceFile != null; } }

        public static void Locate(string ourModDir, string worldKey)
        {
            matesByUid.Clear();
            sourceFile = null;

            try
            {
                string modsDir = Path.GetDirectoryName(ourModDir.TrimEnd('/', '\\'));
                if (modsDir == null) return;

                string ppDir = Path.Combine(modsDir, "PersistentParties");
                if (!Directory.Exists(ppDir))
                {
                    Log.Out("[OfflineRaidProtection] PersistentParties not installed - party_aware will do nothing");
                    return;
                }

                string safe = string.Join("_", worldKey.Split(Path.GetInvalidFileNameChars()));
                string f = Path.Combine(ppDir, safe + ".parties.dat");
                if (!File.Exists(f))
                {
                    Log.Out("[OfflineRaidProtection] no saved parties file yet at " + f);
                    sourceFile = f;   // may appear later
                    return;
                }

                sourceFile = f;
                Reload();
            }
            catch (Exception e)
            {
                Log.Warning("[OfflineRaidProtection] could not locate PersistentParties data: " + e.Message);
            }
        }

        /// <summary>Re-read at most every 30s - parties change while the server runs.</summary>
        private static void ReloadIfStale()
        {
            if (sourceFile == null) return;
            if ((DateTime.UtcNow - lastRead).TotalSeconds < 30) return;
            Reload();
        }

        private static void Reload()
        {
            lastRead = DateTime.UtcNow;
            try
            {
                if (!File.Exists(sourceFile)) { matesByUid.Clear(); return; }

                var fresh = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (string raw in File.ReadAllLines(sourceFile))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] parts = line.Split('|');
                    if (parts.Length < 3) continue;

                    var members = new List<string>();
                    foreach (string u in parts[2].Split(','))
                    {
                        string t = u.Trim();
                        if (t.Length > 0) members.Add(t);
                    }
                    foreach (string u in members) fresh[u] = members;
                }

                matesByUid.Clear();
                foreach (var kv in fresh) matesByUid[kv.Key] = kv.Value;
            }
            catch (Exception e)
            {
                Log.Warning("[OfflineRaidProtection] failed reading parties file: " + e.Message);
            }
        }

        /// <summary>True if any saved party-mate of this player is currently online.</summary>
        public static bool AnyMateOnline(string uid)
        {
            if (uid == null) return false;
            ReloadIfStale();

            List<string> mates;
            if (!matesByUid.TryGetValue(uid, out mates)) return false;

            var ppl = GameManager.Instance != null ? GameManager.Instance.persistentPlayers : null;
            if (ppl == null) return false;

            foreach (string mate in mates)
            {
                if (mate == uid) continue;

                PlatformUserIdentifierAbs id;
                if (!PlatformUserIdentifierAbs.TryFromCombinedString(mate, out id) || id == null) continue;

                PersistentPlayerData data = ppl.GetPlayerData(id);
                if (data != null && data.EntityId != -1) return true;
            }
            return false;
        }
    }
}
