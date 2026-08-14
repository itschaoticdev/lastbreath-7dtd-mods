using System;
using System.Globalization;
using System.IO;

namespace PersistentParties
{
    /// <summary>
    /// Plain key=value config. Deliberately no JSON dependency - the game ships no
    /// serializer we can rely on and a 4-setting file does not justify one.
    /// </summary>
    internal static class Cfg
    {
        public const string FileName = "PersistentParties.cfg";

        public static bool Enabled = true;
        public static float RestoreDelaySeconds = 3f;
        public static bool Announce = true;
        public static int ForgetAfterDays;          // 0 = never forget
        public static bool DebugLog;

        public static void Load(string modDir)
        {
            string path = Path.Combine(modDir, FileName);
            if (!File.Exists(path))
            {
                WriteDefault(path);
                Log.Out("[PersistentParties] wrote default config to " + path);
                return;
            }

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();

                switch (key)
                {
                    case "enabled": Enabled = ParseBool(val, Enabled); break;
                    case "restore_delay_seconds": RestoreDelaySeconds = ParseFloat(val, RestoreDelaySeconds); break;
                    case "announce": Announce = ParseBool(val, Announce); break;
                    case "forget_after_days": ForgetAfterDays = ParseInt(val, ForgetAfterDays); break;
                    case "debug_log": DebugLog = ParseBool(val, DebugLog); break;
                }
            }

            Log.Out(string.Format(
                "[PersistentParties] config: enabled={0} restore_delay={1}s announce={2} forget_after_days={3}",
                Enabled, RestoreDelaySeconds, Announce, ForgetAfterDays));
        }

        private static bool ParseBool(string s, bool fallback)
        {
            bool v;
            return bool.TryParse(s, out v) ? v : fallback;
        }

        private static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private static float ParseFloat(string s, float fallback)
        {
            float v;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private static void WriteDefault(string path)
        {
            try
            {
                File.WriteAllText(path,
@"# PersistentParties configuration
# Edit, then restart the server (values are read once at startup).

# Master switch. false = mod loads but does nothing.
enabled=true

# How long to wait after a player spawns before putting them back in their party.
# Their client needs a moment to finish loading or the party UI can miss the update.
# Raise this if players report joining but seeing an empty party list.
restore_delay_seconds=3.0

# Send a chat message to the party when someone is restored into it.
announce=true

# Forget a party that has had nobody online for this many days. 0 = never forget.
forget_after_days=0

# Verbose logging, for working out why a restore did or did not happen.
debug_log=false
");
            }
            catch (Exception e)
            {
                Log.Error("[PersistentParties] could not write default config: " + e.Message);
            }
        }
    }
}
