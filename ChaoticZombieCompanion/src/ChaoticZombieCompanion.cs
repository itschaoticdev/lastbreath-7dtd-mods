using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace ChaoticZombieCompanion
{
    /// <summary>
    /// Entry point. 7DtD calls InitMod once at startup for every Mods/ folder that ships a DLL.
    ///
    /// The lure itself is pure XML: vanilla already lets a thrown item distract zombies (that is
    /// how a thrown rock works), and the only change is that this item is tagged to be EATEN
    /// rather than merely looked at. Everything XML cannot express is here in code:
    ///
    ///   1. Break an active chase. EAIApproachDistraction.CanExecute refuses to run while the
    ///      zombie holds an attack target, so gore thrown at something already on you would be
    ///      ignored - exactly the moment you need it.
    ///   2. Count finished meals per zombie per player, and convert at the threshold.
    ///   3. Thrall behaviour: follow the owner, fight what hunts the owner, stop chewing on the
    ///      owner's walls, never turn on a player, and come back after a relog.
    ///   4. Keep a thrall out of the save file, so a restart cannot leave a hostile zombie standing
    ///      in your base wearing your name.
    /// </summary>
    public class ChaoticZombieCompanionMod : IModApi
    {
        public const string HarmonyId = "com.breakneck.chaoticzombiecompanion";
        public const string Version = "1.0.0";

        public void InitMod(Mod _modInstance)
        {
            ZcConfig.Load(_modInstance.Path);

            if (!ZcConfig.Enabled)
            {
                Log.Out("[ZombieCompanion] disabled by config, no patches applied.");
                return;
            }

            Harmony harmony;
            try
            {
                harmony = new Harmony(HarmonyId);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
            }
            catch (Exception e)
            {
                Log.Error("[ZombieCompanion] Harmony patching failed, mod is inert: " + e);
                return;
            }

            // Separately, and after the ones conversion depends on: losing the chat hook costs you
            // /thrall, losing the others costs you the whole mod. See ChatHook.
            try
            {
                Patch_GameManager_ChatMessageServer.Apply(harmony);
            }
            catch (Exception e)
            {
                Log.Error("[ZombieCompanion] chat hook could not be applied; /thrall will only work "
                    + "for networked clients on a dedicated server: " + e);
            }

            ModEvents.GameStartDone.RegisterHandler(OnGameStartDone);
            ModEvents.PlayerSpawnedInWorld.RegisterHandler(OnPlayerSpawnedInWorld);
            ModEvents.PlayerDisconnected.RegisterHandler(OnPlayerDisconnected);
            ModEvents.EntityKilled.RegisterHandler(OnEntityKilled);
            ModEvents.ChatMessage.RegisterHandler(OnChatMessage);
            ModEvents.GameUpdate.RegisterHandler(OnGameUpdate);
            ModEvents.WorldShuttingDown.RegisterHandler(OnWorldShuttingDown);

            Log.Out("[ZombieCompanion] v" + Version + " loaded. " + ZcConfig.Describe());
        }

        private static void OnGameStartDone(ref ModEvents.SGameStartDoneData _data)
        {
            // Entity classes exist by now and nothing has spawned yet, so this is both the earliest
            // and the safest point to mark which zombies can be turned. Must run before the
            // self-check, which reports on it.
            ThrallRuntime.Guard("ApplyZombieTags", ThrallRuntime.ApplyZombieTags);

            ThrallRuntime.Guard("SelfCheck", Diag.RunStartupCheck);
            ThrallRuntime.Guard("GameStartDone", ThrallStore.Load);
        }

        private static void OnPlayerSpawnedInWorld(ref ModEvents.SPlayerSpawnedInWorldData _data)
        {
            // Captured into locals: the handler takes its data by ref, so it cannot be used inside
            // a lambda without this.
            int entityId = _data.EntityId;
            RespawnType respawn = _data.RespawnType;
            ThrallRuntime.Guard("PlayerSpawnedInWorld",
                () => ThrallRuntime.OnPlayerSpawned(entityId, respawn));
        }

        private static void OnPlayerDisconnected(ref ModEvents.SPlayerDisconnectedData _data)
        {
            ClientInfo ci = _data.ClientInfo;
            ThrallRuntime.Guard("PlayerDisconnected", () => ThrallRuntime.OnPlayerLeft(ci));
        }

        private static void OnEntityKilled(ref ModEvents.SEntityKilledData _data)
        {
            // KilledEntitiy, not KilledEntity - the typo is vanilla's, in ModEvents.SEntityKilledData.
            Entity killed = _data.KilledEntitiy;
            Entity killer = _data.KillingEntity;
            ThrallRuntime.Guard("EntityKilled", () => ThrallRuntime.OnEntityKilled(killed, killer));
        }

        /// <summary>
        /// Fallback chat path. The real one is the Harmony prefix in ChatHook, which sees the line
        /// before ModEvents dispatch runs at all; this stays registered only in case that patch
        /// ever fails to apply. ThrallCommands.Handle ignores a line it has already acted on this
        /// frame, so having both hooked cannot run a command twice.
        /// </summary>
        private static ModEvents.EModEventResult OnChatMessage(ref ModEvents.SChatMessageData _data)
        {
            ClientInfo ci = _data.ClientInfo;
            int sender = _data.SenderEntityId;
            string msg = _data.Message;

            bool handled = false;
            ThrallRuntime.Guard("ChatMessage", () => { handled = ThrallCommands.Handle(ci, sender, msg); });

            return handled ? ModEvents.EModEventResult.StopHandlersAndVanilla
                           : ModEvents.EModEventResult.Continue;
        }

        private static void OnGameUpdate(ref ModEvents.SGameUpdateData _data)
        {
            ThrallRuntime.Guard("GameUpdate", ThrallRuntime.Tick);
        }

        private static void OnWorldShuttingDown(ref ModEvents.SWorldShuttingDownData _data)
        {
            ThrallRuntime.Guard("WorldShuttingDown", ThrallRuntime.OnShutdown);
        }
    }

    /// <summary>
    /// Plain key=value config, same shape as the other Chaotic mods, so it can be edited without a
    /// JSON/XML dependency. Missing file or missing key falls back to the default.
    /// </summary>
    public static class ZcConfig
    {
        public const string FileName = "ChaoticZombieCompanion.cfg";

        public static bool Enabled = true;

        /// <summary>Finished meals a plain walker needs before it turns. Ripe gore counts double.</summary>
        public static int FeedsToThrall = 4;
        /// <summary>How many meals one Ripe Gore Bait is worth.</summary>
        public static int RipeBaitFeedValue = 2;

        /// <summary>
        /// Extra meals on top of FeedsToThrall for the harder tiers. A radiated zombie carries both
        /// the feral and the radiated tag, so only the highest bonus is applied, never the sum.
        /// </summary>
        public static int FeralFeedBonus = 2;
        public static int RadiatedFeedBonus = 4;
        public static int ChargedFeedBonus = 6;

        /// <summary>How many thralls a player may OWN across their whole roster.</summary>
        public static int MaxOwnedThralls = 6;
        /// <summary>How many of them may stand in the world at once.</summary>
        public static int MaxActiveThralls = 2;

        /// <summary>
        /// How many zombies one thrown bait may occupy. 1 means a horde does not all pile onto the
        /// same pile of gore - the closest one takes it and the rest keep coming. 0 = unlimited
        /// (vanilla decoy behaviour, where every eligible zombie in radius is pulled).
        /// </summary>
        public static int MaxZombiesPerBait = 1;

        /// <summary>
        /// Seconds a baited zombie is forbidden from re-targeting a player. Only needs to outlast
        /// the gap between us clearing the target and EAIApproachDistraction.Start() taking over -
        /// after that vanilla suppresses targeting on its own (EAISetNearestEntityAsTarget.CanExecute
        /// returns false while distraction != null). Generous by default so the escape lands.
        /// </summary>
        public static float PacifySeconds = 8f;

        /// <summary>Whether bait breaks a chase that is already under way.</summary>
        public static bool BreakActiveAggro = true;

        /// <summary>Metres the thrall allows before it starts closing on its owner.</summary>
        public static float FollowDistance = 5f;
        /// <summary>Metres past which the thrall gives up pathing and steps to the owner.</summary>
        public static float TeleportDistance = 45f;

        /// <summary>Thrall max health multiplier. 1 = whatever that zombie tier already had.</summary>
        public static float ThrallHealthScale = 1f;

        /// <summary>Let thralls target players. Off means a thrall never fights a person, ever.</summary>
        public static bool ThrallsAttackPlayers = false;

        /// <summary>
        /// Master switch for zombie-versus-zombie. Off means nothing will ever fight your thrall,
        /// which makes it an untouchable meat grinder rather than a companion.
        /// </summary>
        public static bool ThrallsDrawAggro = true;

        /// <summary>
        /// Metres around a thrall within which the horde will come for IT instead of for you.
        /// 0 turns the active pull off and leaves only retaliation (they fight back when hit).
        /// </summary>
        public static float ThrallTauntRadius = 14f;

        /// <summary>
        /// Fraction of the zombies in that radius that switch to the thrall, rolled ONCE per zombie
        /// so the split is stable rather than creeping to everything over a few seconds. 1 = the
        /// whole horde forgets you exist, which trivialises a blood moon. 0.5 is a wall you can
        /// fight behind, not hide behind.
        /// </summary>
        public static float ThrallTauntShare = 0.5f;

        /// <summary>
        /// Seconds a taunted zombie is held off the player. Refreshed every second while it is
        /// still near the thrall, so this is really "how long aggro survives the thrall dying".
        /// </summary>
        public static float TauntSeconds = 8f;

        /// <summary>
        /// Whether a thrall keeps the block-breaking AI it had as a zombie. Off by default for the
        /// obvious reason: it follows you home.
        /// </summary>
        public static bool ThrallsBreakBlocks = false;

        /// <summary>
        /// Real minutes a thrall lasts before the gore wears off and it goes wild again. 0 = never.
        /// A server-balance lever: on with a low number, thralls are a consumable rather than a pet.
        /// </summary>
        public static float DecayMinutes = 0f;

        /// <summary>Tell the feeder how the conversion is going after every meal.</summary>
        public static bool NotifyProgress = true;

        /// <summary>
        /// Comma-separated entity class names to force in or out of the thrallable set, on top of
        /// the rule the mod applies by itself (every class whose name starts with "zombie", minus
        /// the templates and minus ExcludeZombies).
        /// </summary>
        public static string ExtraThrallable = "";

        /// <summary>
        /// Never turnable. Matched as substrings, so one entry covers a zombie's whole tier ladder.
        ///   Demolition - detonates. It would follow you home and level it.
        ///   Screamer   - its scream summons a horde ON its position, i.e. on you.
        /// A config that sets this to an empty string really does clear it: the pair below are the
        /// default rather than a floor, so a server that wants the carnage can have it.
        /// </summary>
        public static string ExcludeZombies = "zombieDemolition,zombieScreamer";

        public static bool Debug = false;

        private static readonly char[] Sep = { '=' };

        public static void Load(string modPath)
        {
            string path = Path.Combine(modPath, FileName);
            if (!File.Exists(path))
            {
                Log.Out("[ZombieCompanion] no " + FileName + " found, using defaults.");
                return;
            }

            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                    string[] kv = line.Split(Sep, 2);
                    if (kv.Length != 2) continue;

                    string k = kv[0].Trim();
                    string v = kv[1].Trim();

                    switch (k.ToLowerInvariant())
                    {
                        case "enabled":             Enabled = ParseBool(v, Enabled); break;
                        case "feedstothrall":       FeedsToThrall = ParseInt(v, FeedsToThrall); break;
                        case "ripebaitfeedvalue":   RipeBaitFeedValue = ParseInt(v, RipeBaitFeedValue); break;
                        case "feralfeedbonus":      FeralFeedBonus = ParseInt(v, FeralFeedBonus); break;
                        case "radiatedfeedbonus":   RadiatedFeedBonus = ParseInt(v, RadiatedFeedBonus); break;
                        case "chargedfeedbonus":    ChargedFeedBonus = ParseInt(v, ChargedFeedBonus); break;
                        case "maxownedthralls":     MaxOwnedThralls = ParseInt(v, MaxOwnedThralls); break;
                        case "maxactivethralls":    MaxActiveThralls = ParseInt(v, MaxActiveThralls); break;
                        case "maxzombiesperbait":   MaxZombiesPerBait = ParseInt(v, MaxZombiesPerBait); break;
                        case "pacifyseconds":       PacifySeconds = ParseFloat(v, PacifySeconds); break;
                        case "breakactiveaggro":    BreakActiveAggro = ParseBool(v, BreakActiveAggro); break;
                        case "followdistance":      FollowDistance = ParseFloat(v, FollowDistance); break;
                        case "teleportdistance":    TeleportDistance = ParseFloat(v, TeleportDistance); break;
                        case "thrallhealthscale":   ThrallHealthScale = ParseFloat(v, ThrallHealthScale); break;
                        case "thrallsattackplayers":ThrallsAttackPlayers = ParseBool(v, ThrallsAttackPlayers); break;
                        case "thrallsdrawaggro":    ThrallsDrawAggro = ParseBool(v, ThrallsDrawAggro); break;
                        case "thralltauntradius":   ThrallTauntRadius = ParseFloat(v, ThrallTauntRadius); break;
                        case "thralltauntshare":    ThrallTauntShare = ParseFloat(v, ThrallTauntShare); break;
                        case "tauntseconds":        TauntSeconds = ParseFloat(v, TauntSeconds); break;
                        case "thrallsbreakblocks":  ThrallsBreakBlocks = ParseBool(v, ThrallsBreakBlocks); break;
                        case "decayminutes":        DecayMinutes = ParseFloat(v, DecayMinutes); break;
                        case "notifyprogress":      NotifyProgress = ParseBool(v, NotifyProgress); break;
                        case "extrathrallable":     ExtraThrallable = v; break;
                        case "excludezombies":      ExcludeZombies = v; break;
                        case "debug":               Debug = ParseBool(v, Debug); break;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("[ZombieCompanion] failed reading " + FileName + ", using defaults: " + e.Message);
            }

            if (FeedsToThrall < 1) FeedsToThrall = 1;
            if (RipeBaitFeedValue < 1) RipeBaitFeedValue = 1;
            if (FeralFeedBonus < 0) FeralFeedBonus = 0;
            if (RadiatedFeedBonus < 0) RadiatedFeedBonus = 0;
            if (ChargedFeedBonus < 0) ChargedFeedBonus = 0;
            if (MaxOwnedThralls < 0) MaxOwnedThralls = 0;
            if (MaxActiveThralls < 1) MaxActiveThralls = 1;
            if (MaxActiveThralls > MaxOwnedThralls) MaxActiveThralls = MaxOwnedThralls;
            if (MaxZombiesPerBait < 0) MaxZombiesPerBait = 0;
            if (FollowDistance < 2f) FollowDistance = 2f;
            if (TeleportDistance < FollowDistance * 2f) TeleportDistance = FollowDistance * 2f;
            if (ThrallHealthScale < 0.1f) ThrallHealthScale = 0.1f;
            if (DecayMinutes < 0f) DecayMinutes = 0f;
            if (ThrallTauntRadius < 0f) ThrallTauntRadius = 0f;
            if (ThrallTauntShare < 0f) ThrallTauntShare = 0f;
            if (ThrallTauntShare > 1f) ThrallTauntShare = 1f;
            if (TauntSeconds < 1f) TauntSeconds = 1f;
        }

        public static string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "feeds={0} (+{1}/+{2}/+{3} feral/rad/charged) ripeWorth={4} roster={5} out={6} perBait={7} "
                + "pacify={8}s follow={9}m teleport={10}m hpScale={11} taunt={12}({13}m/{14}/{15}s) "
                + "breakBlocks={16} decay={17}min",
                FeedsToThrall, FeralFeedBonus, RadiatedFeedBonus, ChargedFeedBonus, RipeBaitFeedValue,
                MaxOwnedThralls, MaxActiveThralls, MaxZombiesPerBait, PacifySeconds, FollowDistance,
                TeleportDistance, ThrallHealthScale, ThrallsDrawAggro, ThrallTauntRadius,
                ThrallTauntShare, TauntSeconds, ThrallsBreakBlocks, DecayMinutes);
        }

        private static bool ParseBool(string v, bool def)
        {
            bool r;
            return bool.TryParse(v, out r) ? r : def;
        }

        private static float ParseFloat(string v, float def)
        {
            float r;
            return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r) ? r : def;
        }

        private static int ParseInt(string v, int def)
        {
            int r;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : def;
        }
    }
}
