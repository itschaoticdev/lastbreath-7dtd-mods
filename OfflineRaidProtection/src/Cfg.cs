using System;
using System.Globalization;
using System.IO;

namespace OfflineRaidProtection
{
    internal static class Cfg
    {
        public const string FileName = "OfflineRaidProtection.cfg";

        public static bool Enabled = true;
        /// <summary>"immune" = effectively indestructible, "multiplier" = use ProtectionMultiplier.</summary>
        public static string Mode = "immune";
        public static float ProtectionMultiplier = 32f;
        public static int GraceMinutes = 10;
        public static bool PartyAware = true;
        public static string RaidWindow = "";         // "18:00-23:00", empty = protected around the clock
        public static bool DebugLog;

        /// <summary>Hardness multiplier used for "immune". Not float.MaxValue: the game
        /// multiplies this into damage maths and infinities produce NaN block states.</summary>
        public const float ImmuneMultiplier = 1000000f;

        public static void Load(string modDir)
        {
            string path = Path.Combine(modDir, FileName);
            if (!File.Exists(path)) { WriteDefault(path); Log.Out("[OfflineRaidProtection] wrote default config to " + path); return; }

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();

                switch (k)
                {
                    case "enabled": Enabled = Bool(v, Enabled); break;
                    case "mode": Mode = v.ToLowerInvariant(); break;
                    case "protection_multiplier": ProtectionMultiplier = Flt(v, ProtectionMultiplier); break;
                    case "grace_minutes": GraceMinutes = Int(v, GraceMinutes); break;
                    case "party_aware": PartyAware = Bool(v, PartyAware); break;
                    case "raid_window": RaidWindow = v; break;
                    case "debug_log": DebugLog = Bool(v, DebugLog); break;
                }
            }

            if (Mode != "immune" && Mode != "multiplier")
            {
                Log.Warning("[OfflineRaidProtection] unknown mode '" + Mode + "', falling back to immune");
                Mode = "immune";
            }

            Log.Out(string.Format("[OfflineRaidProtection] config: enabled={0} mode={1} multiplier={2} grace={3}min party_aware={4} raid_window='{5}'",
                Enabled, Mode, ProtectionMultiplier, GraceMinutes, PartyAware, RaidWindow));
        }

        private static bool Bool(string s, bool f) { bool v; return bool.TryParse(s, out v) ? v : f; }
        private static int Int(string s, int f) { int v; return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : f; }
        private static float Flt(string s, float f) { float v; return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : f; }

        private static void WriteDefault(string path)
        {
            try
            {
                File.WriteAllText(path,
@"# OfflineRaidProtection configuration
# Edit, then restart the server (values are read once at startup).

# Master switch. false = mod loads but does nothing, vanilla rules apply.
enabled=true

# What happens to a claimed base while its owner is offline:
#   immune      - effectively indestructible
#   multiplier  - blocks get protection_multiplier times their normal durability
mode=immune

# Only used when mode=multiplier. Vanilla's own offline setting for comparison is
# LandClaimOfflineDurabilityModifier (default 32).
protection_multiplier=32

# Protection does not kick in until the owner has been offline this many minutes.
# Stops someone alt-F4ing mid-raid to turn their base invincible.
grace_minutes=10

# Treat the base as ONLINE (i.e. raidable) if any member of the owner's saved party
# is online. Needs the PersistentParties mod installed alongside this one; without
# it this setting does nothing. Prevents a clan parking one member offline as a shield.
party_aware=true

# Optional raid window in real server time, 24h, e.g. 18:00-23:00.
# Inside the window, offline bases are raidable as normal. Outside it they are
# protected. Leave empty to protect offline bases around the clock.
# A window that crosses midnight (22:00-02:00) works.
raid_window=

# Verbose logging - prints a line per protection decision. Noisy; for testing only.
debug_log=false
");
            }
            catch (Exception e) { Log.Error("[OfflineRaidProtection] could not write default config: " + e.Message); }
        }

        /// <summary>True when the configured raid window is open right now (or no window is set to false).</summary>
        public static bool RaidWindowOpen()
        {
            if (string.IsNullOrEmpty(RaidWindow)) return false;

            int dash = RaidWindow.IndexOf('-');
            if (dash <= 0) return false;

            int from, to;
            if (!ParseHhMm(RaidWindow.Substring(0, dash), out from)) return false;
            if (!ParseHhMm(RaidWindow.Substring(dash + 1), out to)) return false;

            DateTime now = DateTime.Now;
            int mins = now.Hour * 60 + now.Minute;

            // A window like 22:00-02:00 wraps past midnight.
            return from <= to ? (mins >= from && mins < to) : (mins >= from || mins < to);
        }

        private static bool ParseHhMm(string s, out int minutes)
        {
            minutes = 0;
            s = s.Trim();
            int c = s.IndexOf(':');
            if (c <= 0) return false;
            int h, m;
            if (!int.TryParse(s.Substring(0, c), out h)) return false;
            if (!int.TryParse(s.Substring(c + 1), out m)) return false;
            if (h < 0 || h > 23 || m < 0 || m > 59) return false;
            minutes = h * 60 + m;
            return true;
        }
    }
}
