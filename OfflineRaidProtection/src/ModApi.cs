using System;
using System.Reflection;
using HarmonyLib;

namespace OfflineRaidProtection
{
    public class ModApi : IModApi
    {
        internal static string ModDir;

        public void InitMod(Mod _modInstance)
        {
            try { Init(_modInstance); }
            catch (Exception e) { Log.Error("[OfflineRaidProtection] InitMod failed: " + e); }
        }

        private static void Init(Mod _modInstance)
        {
            // Mod.Path, not Assembly.Location - mod assemblies are loaded from a byte[]
            // so Location is empty.
            ModDir = _modInstance != null ? _modInstance.Path : null;
            if (string.IsNullOrEmpty(ModDir))
            {
                Log.Error("[OfflineRaidProtection] could not determine mod folder - aborting");
                return;
            }

            Cfg.Load(ModDir);
            if (!Cfg.Enabled)
            {
                Log.Out("[OfflineRaidProtection] disabled in config - not hooking anything");
                return;
            }

            new Harmony("com.chaotic.offlineraidprotection").PatchAll(Assembly.GetExecutingAssembly());
            ModEvents.GameStartDone.RegisterHandler(OnGameStartDone);

            Log.Out("[OfflineRaidProtection] loaded");
        }

        private static void OnGameStartDone(ref ModEvents.SGameStartDoneData _d)
        {
            if (!IsServer) return;
            string world = GamePrefs.GetString(EnumGamePrefs.GameName);
            if (string.IsNullOrEmpty(world)) world = "default";
            if (Cfg.PartyAware) ClanLink.Locate(ModDir, world);
        }

        internal static bool IsServer
        {
            get
            {
                return SingletonMonoBehaviour<ConnectionManager>.Instance != null
                       && SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer;
            }
        }
    }

    /// <summary>
    /// Vanilla already multiplies the durability of blocks inside a land claim by a
    /// factor that depends on whether the owner is online. That single float is the
    /// whole protection system, so we postfix it rather than trying to intercept
    /// block damage itself - no damage maths to reimplement and no fighting the game.
    /// </summary>
    [HarmonyPatch(typeof(World), nameof(World.GetLandProtectionHardnessModifierForPlayer))]
    internal static class Patch_LandProtectionHardness
    {
        private static void Postfix(PersistentPlayerData ppData, ref float __result)
        {
            if (!Cfg.Enabled || ppData == null) return;

            // Owner is online -> vanilla behaviour, base is raidable as normal.
            if (ppData.EntityId != -1) return;

            // A clanmate being online counts as the base being defended, otherwise a
            // group just parks one member offline and the base is permanently safe.
            if (Cfg.PartyAware && ClanLink.AnyMateOnline(SafeUid(ppData)))
            {
                if (Cfg.DebugLog) Log.Out("[OfflineRaidProtection] " + Name(ppData) + " offline but a partymate is on - not protecting");
                return;
            }

            // Grace period: stops someone disconnecting mid-raid to go invincible.
            if (Cfg.GraceMinutes > 0 && ppData.OfflineMinutes < Cfg.GraceMinutes)
            {
                if (Cfg.DebugLog) Log.Out("[OfflineRaidProtection] " + Name(ppData) + " only offline " +
                                          (int)ppData.OfflineMinutes + "min - still inside grace");
                return;
            }

            // Scheduled raid hours: inside the window, offline bases are fair game.
            if (Cfg.RaidWindowOpen())
            {
                if (Cfg.DebugLog) Log.Out("[OfflineRaidProtection] raid window open - not protecting " + Name(ppData));
                return;
            }

            float protectedValue = Cfg.Mode == "immune" ? Cfg.ImmuneMultiplier : Cfg.ProtectionMultiplier;

            // Never make a base weaker than vanilla would have.
            if (protectedValue <= __result) return;

            if (Cfg.DebugLog)
                Log.Out("[OfflineRaidProtection] protecting " + Name(ppData) + " (offline " +
                        (int)ppData.OfflineMinutes + "min): hardness x" + __result + " -> x" + protectedValue);

            __result = protectedValue;
        }

        private static string SafeUid(PersistentPlayerData d)
        {
            try { return d.PrimaryId != null ? d.PrimaryId.CombinedString : null; }
            catch { return null; }
        }

        private static string Name(PersistentPlayerData d)
        {
            try { return d.PlayerName != null ? d.PlayerName.ToString() : "?"; }
            catch { return "?"; }
        }
    }
}
