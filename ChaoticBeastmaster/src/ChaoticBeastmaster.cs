using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace ChaoticBeastmaster
{
    /// <summary>
    /// Entry point. 7DtD calls InitMod once at startup for every Mods/ folder that ships a DLL.
    ///
    /// Everything here is server authority only. The bait itself is pure XML (vanilla's thrown
    /// decoy mechanic re-pointed at animals via DistractionTags), so the only jobs left for code
    /// are the four things XML cannot express:
    ///
    ///   1. Give tameable animals the ApproachDistraction AI task. Vanilla only hands it to the
    ///      zombie animals, and doing it in XML would mean restating each animal's whole AITask
    ///      list - which breaks the moment TFP retune one. Done at spawn instead.
    ///   2. Break an active chase. EAIApproachDistraction.CanExecute refuses to run while the
    ///      animal holds an attack target, so bait thrown at something already hunting you would
    ///      be ignored - exactly the moment you need it.
    ///   3. Count finished meals per animal per player, and tame at the threshold.
    ///   4. Companion behaviour: follow the owner, fight what the owner fights, never turn on a
    ///      player, and come back after a relog.
    /// </summary>
    public class ChaoticBeastmasterMod : IModApi
    {
        public const string HarmonyId = "com.breakneck.chaoticbeastmaster";
        public const string Version = "1.2.3";

        public void InitMod(Mod _modInstance)
        {
            BmConfig.Load(_modInstance.Path);

            if (!BmConfig.Enabled)
            {
                Log.Out("[Beastmaster] disabled by config, no patches applied.");
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
                Log.Error("[Beastmaster] Harmony patching failed, mod is inert: " + e);
                return;
            }

            // Separately, and after the ones taming depends on: losing the chat hook costs you
            // /pet, losing the others costs you the whole mod. See ChatHook.
            try
            {
                Patch_GameManager_ChatMessageServer.Apply(harmony);
            }
            catch (Exception e)
            {
                Log.Error("[Beastmaster] chat hook could not be applied; /pet will only work for "
                    + "networked clients on a dedicated server: " + e);
            }

            ModEvents.GameStartDone.RegisterHandler(OnGameStartDone);
            ModEvents.PlayerSpawnedInWorld.RegisterHandler(OnPlayerSpawnedInWorld);
            ModEvents.PlayerDisconnected.RegisterHandler(OnPlayerDisconnected);
            ModEvents.EntityKilled.RegisterHandler(OnEntityKilled);
            ModEvents.ChatMessage.RegisterHandler(OnChatMessage);
            ModEvents.GameUpdate.RegisterHandler(OnGameUpdate);
            ModEvents.WorldShuttingDown.RegisterHandler(OnWorldShuttingDown);

            Log.Out("[Beastmaster] v" + Version + " loaded. " + BmConfig.Describe());
        }

        private static void OnGameStartDone(ref ModEvents.SGameStartDoneData _data)
        {
            // Entity classes exist by now and no animal has spawned yet, so this is both the
            // earliest and the safest point to guarantee the tags regardless of what the xpath in
            // entityclasses.xml managed to do. Must run before the self-check, which reports on it.
            TameRuntime.Guard("ApplyAnimalTags", TameRuntime.ApplyAnimalTags);

            // Items and entity classes are only loaded by now, so this is the earliest point the
            // self-check can see whether the XML actually landed.
            TameRuntime.Guard("SelfCheck", Diag.RunStartupCheck);
            TameRuntime.Guard("GameStartDone", PetStore.Load);
        }

        private static void OnPlayerSpawnedInWorld(ref ModEvents.SPlayerSpawnedInWorldData _data)
        {
            // Captured into locals: the handler takes its data by ref, so it cannot be used
            // inside a lambda without this.
            int entityId = _data.EntityId;
            RespawnType respawn = _data.RespawnType;
            TameRuntime.Guard("PlayerSpawnedInWorld", () => TameRuntime.OnPlayerSpawned(entityId, respawn));
        }

        private static void OnPlayerDisconnected(ref ModEvents.SPlayerDisconnectedData _data)
        {
            ClientInfo ci = _data.ClientInfo;
            TameRuntime.Guard("PlayerDisconnected", () => TameRuntime.OnPlayerLeft(ci));
        }

        private static void OnEntityKilled(ref ModEvents.SEntityKilledData _data)
        {
            // KilledEntitiy, not KilledEntity - the typo is vanilla's, in ModEvents.SEntityKilledData.
            Entity killed = _data.KilledEntitiy;
            Entity killer = _data.KillingEntity;
            TameRuntime.Guard("EntityKilled", () => TameRuntime.OnEntityKilled(killed, killer));
        }

        /// <summary>
        /// Fallback chat path. The real one is the Harmony prefix in ChatHook, which sees the line
        /// before ModEvents dispatch runs at all; this stays registered only in case that patch
        /// ever fails to apply. PetCommands.Handle ignores a line it has already acted on this
        /// frame, so having both hooked cannot run a command twice.
        ///
        /// Returning StopHandlersAndVanilla swallows the command so it does not get broadcast to
        /// everyone else as a chat line.
        /// </summary>
        private static ModEvents.EModEventResult OnChatMessage(ref ModEvents.SChatMessageData _data)
        {
            ClientInfo ci = _data.ClientInfo;
            int sender = _data.SenderEntityId;
            string msg = _data.Message;

            bool handled = false;
            TameRuntime.Guard("ChatMessage", () => { handled = PetCommands.Handle(ci, sender, msg); });

            return handled ? ModEvents.EModEventResult.StopHandlersAndVanilla
                           : ModEvents.EModEventResult.Continue;
        }

        private static void OnGameUpdate(ref ModEvents.SGameUpdateData _data)
        {
            TameRuntime.Guard("GameUpdate", TameRuntime.Tick);
        }

        private static void OnWorldShuttingDown(ref ModEvents.SWorldShuttingDownData _data)
        {
            TameRuntime.Guard("WorldShuttingDown", TameRuntime.OnShutdown);
        }
    }

    /// <summary>
    /// Plain key=value config, same shape as the other Chaotic mods, so it can be edited without
    /// a JSON/XML dependency. Missing file or missing key falls back to the default.
    /// </summary>
    public static class BmConfig
    {
        public const string FileName = "ChaoticBeastmaster.cfg";

        public static bool Enabled = true;

        /// <summary>Finished meals needed before an animal turns. Prime bait counts double.</summary>
        public static int FeedsToTame = 5;
        /// <summary>How many meals one Prime Bait is worth.</summary>
        public static int PrimeBaitFeedValue = 2;
        /// <summary>
        /// How many companions a player may OWN (the kennel). Only ever one of them is out at a
        /// time - the rest wait until called with /pet call.
        /// </summary>
        public static int MaxOwnedPets = 5;

        /// <summary>
        /// How many animals one thrown bait may occupy. 1 means a pack does not all pile onto the
        /// same slab - the closest one takes it and the rest keep hunting you. 0 = unlimited
        /// (vanilla decoy behaviour, where every eligible animal in radius is pulled).
        /// </summary>
        public static int MaxAnimalsPerBait = 1;

        /// <summary>
        /// Seconds a baited animal is forbidden from re-targeting a player. Only needs to outlast
        /// the gap between us clearing the target and EAIApproachDistraction.Start() taking over -
        /// after that vanilla suppresses targeting on its own (EAISetNearestEntityAsTarget bails
        /// while distraction != null). Generous by default so the escape actually feels like one.
        /// </summary>
        public static float PacifySeconds = 8f;

        /// <summary>Whether bait breaks a chase that is already under way.</summary>
        public static bool BreakActiveAggro = true;

        /// <summary>Metres the companion allows before it starts closing on its owner.</summary>
        public static float FollowDistance = 6f;
        /// <summary>Metres past which the companion gives up pathing and teleports to the owner.</summary>
        public static float TeleportDistance = 45f;

        /// <summary>Companion max health multiplier. 1 = whatever the wild animal had.</summary>
        public static float PetHealthScale = 1.5f;

        /// <summary>Let companions target players. Off means a pet never fights a person, ever.</summary>
        public static bool PetsAttackPlayers = false;

        /// <summary>Tell the feeder how the taming is going after every meal.</summary>
        public static bool NotifyProgress = true;

        /// <summary>
        /// Extra entity classes to make tameable / baitable, on top of the built-in list, as a
        /// comma-separated list of entity class names (e.g. "animalSnake,animalBoar"). Same job as
        /// adding a tag in entityclasses.xml, for anyone who would rather not edit XML.
        /// </summary>
        public static string ExtraTameable = "";
        public static string ExtraBaitable = "";

        public static bool Debug = false;

        private static readonly char[] Sep = { '=' };

        public static void Load(string modPath)
        {
            string path = Path.Combine(modPath, FileName);
            if (!File.Exists(path))
            {
                Log.Out("[Beastmaster] no " + FileName + " found, using defaults.");
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
                        case "enabled":            Enabled = ParseBool(v, Enabled); break;
                        case "feedstotame":        FeedsToTame = ParseInt(v, FeedsToTame); break;
                        case "primebaitfeedvalue": PrimeBaitFeedValue = ParseInt(v, PrimeBaitFeedValue); break;
                        // maxpetsperplayer is the 1.0.0 name, kept so old cfgs keep working.
                        case "maxpetsperplayer":
                        case "maxownedpets":       MaxOwnedPets = ParseInt(v, MaxOwnedPets); break;
                        case "maxanimalsperbait":  MaxAnimalsPerBait = ParseInt(v, MaxAnimalsPerBait); break;
                        case "pacifyseconds":      PacifySeconds = ParseFloat(v, PacifySeconds); break;
                        case "breakactiveaggro":   BreakActiveAggro = ParseBool(v, BreakActiveAggro); break;
                        case "followdistance":     FollowDistance = ParseFloat(v, FollowDistance); break;
                        case "teleportdistance":   TeleportDistance = ParseFloat(v, TeleportDistance); break;
                        case "pethealthscale":     PetHealthScale = ParseFloat(v, PetHealthScale); break;
                        case "petsattackplayers":  PetsAttackPlayers = ParseBool(v, PetsAttackPlayers); break;
                        case "notifyprogress":     NotifyProgress = ParseBool(v, NotifyProgress); break;
                        case "extratameable":      ExtraTameable = v; break;
                        case "extrabaitable":      ExtraBaitable = v; break;
                        case "debug":              Debug = ParseBool(v, Debug); break;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("[Beastmaster] failed reading " + FileName + ", using defaults: " + e.Message);
            }

            if (FeedsToTame < 1) FeedsToTame = 1;
            if (PrimeBaitFeedValue < 1) PrimeBaitFeedValue = 1;
            if (MaxOwnedPets < 0) MaxOwnedPets = 0;
            if (MaxAnimalsPerBait < 0) MaxAnimalsPerBait = 0;
            if (FollowDistance < 2f) FollowDistance = 2f;
            if (TeleportDistance < FollowDistance * 2f) TeleportDistance = FollowDistance * 2f;
        }

        public static string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "feedsToTame={0} primeWorth={1} kennel={2} (1 out at a time) perBait={3} pacify={4}s follow={5}m teleport={6}m hpScale={7}",
                FeedsToTame, PrimeBaitFeedValue, MaxOwnedPets, MaxAnimalsPerBait, PacifySeconds,
                FollowDistance, TeleportDistance, PetHealthScale);
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
