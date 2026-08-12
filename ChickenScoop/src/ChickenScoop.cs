using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ChickenScoop
{
    /// <summary>
    /// Entry point. 7DtD calls InitMod once at startup for every Mods/ folder that ships a DLL.
    /// </summary>
    public class ChickenScoopMod : IModApi
    {
        public const string HarmonyId = "com.breakneck.chickenscoop";

        public void InitMod(Mod _modInstance)
        {
            ScoopConfig.Load(_modInstance.Path);

            if (!ScoopConfig.Enabled)
            {
                Log.Out("[ChickenScoop] disabled by config, no patches applied.");
                return;
            }

            new Harmony(HarmonyId).PatchAll(Assembly.GetExecutingAssembly());
            Log.Out("[ChickenScoop] v1.0.2 loaded. " + ScoopConfig.Describe());
        }
    }

    /// <summary>
    /// Plain key=value config so it can be edited without a JSON/XML dependency.
    /// Missing file or missing key falls back to the default.
    /// </summary>
    public static class ScoopConfig
    {
        public const string FileName = "ChickenScoop.cfg";

        public static bool Enabled = true;
        /// <summary>Require the vanilla Vehicle Plow Mod to be installed (the visible blade doubles as the scoop).</summary>
        public static bool RequirePlowMod = true;
        /// <summary>Vehicle mod tag that acts as the scoop. "plow" = vanilla modVehiclePlow.</summary>
        public static string ScoopModTag = "plow";
        /// <summary>Entity tags scanned for scoopable animals.</summary>
        public static string TargetEntityTags = "chicken";
        /// <summary>Metres ahead of the vehicle centre to place the scan box.</summary>
        public static float ScanForwardOffset = 1.6f;
        /// <summary>Half-width/half-depth of the scan box, in metres.</summary>
        public static float ScanRadius = 2.2f;
        /// <summary>Vehicle must be moving at least this fast (m/s) to scoop.</summary>
        public static float MinSpeed = 2.0f;
        /// <summary>Seconds between scans per vehicle.</summary>
        public static float ScanInterval = 0.15f;
        /// <summary>Max birds taken per scan, so a flock does not vanish in one frame.</summary>
        public static int MaxPerScan = 2;
        public static bool PlaySound = true;
        public static bool NotifyDriver = true;
        /// <summary>Log a throttled line per vehicle explaining why a scan did or did not scoop.</summary>
        public static bool Debug = false;

        private static readonly char[] Sep = { '=' };

        public static void Load(string modPath)
        {
            string path = Path.Combine(modPath, FileName);
            if (!File.Exists(path))
            {
                Log.Out("[ChickenScoop] no " + FileName + " found, using defaults.");
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
                        case "requireplowmod":     RequirePlowMod = ParseBool(v, RequirePlowMod); break;
                        case "scoopmodtag":        ScoopModTag = v; break;
                        case "targetentitytags":   TargetEntityTags = v; break;
                        case "scanforwardoffset":  ScanForwardOffset = ParseFloat(v, ScanForwardOffset); break;
                        case "scanradius":         ScanRadius = ParseFloat(v, ScanRadius); break;
                        case "minspeed":           MinSpeed = ParseFloat(v, MinSpeed); break;
                        case "scaninterval":       ScanInterval = ParseFloat(v, ScanInterval); break;
                        case "maxperscan":         MaxPerScan = ParseInt(v, MaxPerScan); break;
                        case "playsound":          PlaySound = ParseBool(v, PlaySound); break;
                        case "notifydriver":       NotifyDriver = ParseBool(v, NotifyDriver); break;
                        case "debug":              Debug = ParseBool(v, Debug); break;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("[ChickenScoop] failed reading " + FileName + ", using defaults: " + e.Message);
            }
        }

        public static string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "requirePlowMod={0} scoopModTag={1} targets={2} radius={3} minSpeed={4}",
                RequirePlowMod, ScoopModTag, TargetEntityTags, ScanRadius, MinSpeed);
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

    /// <summary>
    /// EntityVehicle.Update runs every frame per vehicle on the server. The postfix is throttled
    /// per vehicle and bails on the cheapest checks first.
    /// </summary>
    [HarmonyPatch(typeof(EntityVehicle), "Update")]
    public static class Patch_EntityVehicle_Update
    {
        private static bool loggedFailure;

        [HarmonyPostfix]
        public static void Postfix(EntityVehicle __instance)
        {
            try
            {
                ScoopRuntime.Tick(__instance);
            }
            catch (Exception e)
            {
                // A throwing postfix every frame would flood the log, so report once and stay quiet.
                if (!loggedFailure)
                {
                    loggedFailure = true;
                    Log.Error("[ChickenScoop] scoop tick failed (further errors suppressed): " + e);
                }
            }
        }
    }

    public static class ScoopRuntime
    {
        /// <summary>
        /// Per-vehicle scan bookkeeping. Speed and heading are derived from how far the vehicle
        /// actually moved between scans rather than from its rigidbody, because on a dedicated
        /// server a player-driven vehicle has no live rigidbody at all - see Tick().
        /// </summary>
        private struct ScanState
        {
            public float NextScanTime;
            public float LastScanTime;
            public Vector3 LastPos;
            public bool HasLastPos;
        }

        /// <summary>Scan bookkeeping keyed by vehicle entity id.</summary>
        private static readonly Dictionary<int, ScanState> States = new Dictionary<int, ScanState>();

        /// <summary>Vehicles we already told the driver were full, so the tooltip fires once per fill.</summary>
        private static readonly HashSet<int> WarnedFull = new HashSet<int>();

        /// <summary>Pickup items we already reported as unstorable, so the log gets one line each.</summary>
        private static readonly HashSet<string> WarnedRestricted = new HashSet<string>();

        private static readonly List<Entity> ScanBuffer = new List<Entity>();

        private static bool tagsReady;
        private static FastTags<TagGroup.Global> targetTags;
        private static FastTags<TagGroup.Global> scoopTag;

        private static void EnsureTags()
        {
            if (tagsReady) return;
            targetTags = FastTags<TagGroup.Global>.Parse(ScoopConfig.TargetEntityTags);
            scoopTag = FastTags<TagGroup.Global>.Parse(ScoopConfig.ScoopModTag);
            tagsReady = true;
        }

        public static void Tick(EntityVehicle v)
        {
            if (v == null) return;

            // Server authority only: the host owns entity removal and vehicle storage.
            //
            // Deliberately NOT gated on isEntityRemote. On a dedicated server every connected
            // player's EntityPlayer is created with isEntityRemote = true, and Entity.AttachToEntity
            // copies the driver's flag onto the vehicle when it fills seat 0. So the moment a player
            // drives, the vehicle is "remote" on the server as well - the flag means "someone else
            // simulates the physics", not "someone else owns the world". IsServer is the authority
            // test; that is the one that matters here.
            var cm = SingletonMonoBehaviour<ConnectionManager>.Instance;
            if (cm == null || !cm.IsServer) return;

            if (!v.HasDriver)
            {
                States.Remove(v.entityId);
                WarnedFull.Remove(v.entityId);
                return;
            }

            World world = v.world;
            if (world == null) return;

            // Throttle before doing anything expensive.
            float now = Time.time;
            ScanState st;
            if (!States.TryGetValue(v.entityId, out st)) st = new ScanState();
            if (now < st.NextScanTime) return;

            Vector3 pos = v.position;
            float dt = st.HasLastPos ? now - st.LastScanTime : 0f;
            Vector3 travel = st.HasLastPos ? pos - st.LastPos : Vector3.zero;

            Vector3 prevPos = st.HasLastPos ? st.LastPos : pos;
            st.NextScanTime = now + ScoopConfig.ScanInterval;
            st.LastScanTime = now;
            st.LastPos = pos;
            st.HasLastPos = true;
            States[v.entityId] = st;

            // A chunk unload, a teleport or a long stall makes the delta meaningless - skip one scan.
            if (dt <= 0.0001f || dt > 1f || travel.sqrMagnitude > 900f) return;

            // Speed from the position delta, not GetVelocityPerSecond(). A remote vehicle's
            // rigidbody is forced kinematic on the server (EntityVehicle.PhysicsFixedUpdate), so
            // the rigidbody path reads zero and the synced-velocity path only updates while a sync
            // packet is mid-playback. Where the vehicle actually was, and now is, is always true.
            float speed = travel.magnitude / dt;
            if (speed < ScoopConfig.MinSpeed) return;

            EnsureTags();

            // The scoop itself: vanilla Vehicle Plow Mod puts a real blade on the 4x4's bumper.
            if (ScoopConfig.RequirePlowMod)
            {
                Vehicle veh = v.vehicle;
                if (veh == null || !veh.ModTags.Test_AnySet(scoopTag))
                {
                    if (ScoopConfig.Debug) DebugOnce(v.entityId, "no '" + ScoopConfig.ScoopModTag + "' mod tag on this vehicle");
                    return;
                }
            }

            // Birds ride in the vehicle's storage, which only exists with the Storage mod fitted.
            Bag bag = v.bag;
            if (bag == null || bag.SlotCount == 0)
            {
                if (ScoopConfig.Debug) DebugOnce(v.entityId, "no vehicle storage (Storage mod not fitted)");
                return;
            }

            // Heading comes from the travel vector too. transform.forward is only a fallback: on the
            // server a remote vehicle's transform lags the synced rotation.
            Vector3 forward = travel.sqrMagnitude > 0.0025f
                ? travel.normalized
                : (v.transform != null ? v.transform.forward : Vector3.forward);

            float r = ScoopConfig.ScanRadius;
            Vector3 size = new Vector3(r * 2f, 2.5f, r * 2f);

            // Sweep from where the bumper was at the last scan to where it is now, so a fast truck
            // cannot tunnel a bird between two scans - at 15 m/s a 0.15 s gap is over 2 m of road.
            var box = new Bounds(pos + forward * ScoopConfig.ScanForwardOffset + Vector3.up * 0.5f, size);
            box.Encapsulate(new Bounds(prevPos + forward * ScoopConfig.ScanForwardOffset + Vector3.up * 0.5f, size));

            ScanBuffer.Clear();
            world.GetEntitiesInBounds(targetTags, box, ScanBuffer);

            if (ScoopConfig.Debug)
            {
                Log.Out(string.Format(CultureInfo.InvariantCulture,
                    "[ChickenScoop] veh {0} speed={1:0.0}m/s bagSlots={2} candidates={3}",
                    v.entityId, speed, bag.SlotCount, ScanBuffer.Count));
            }

            if (ScanBuffer.Count == 0)
            {
                WarnedFull.Remove(v.entityId);
                return;
            }

            int scooped = 0;
            bool full = false;

            for (int i = 0; i < ScanBuffer.Count && scooped < ScoopConfig.MaxPerScan; i++)
            {
                Entity e = ScanBuffer[i];
                if (e == null || e == v) continue;

                var alive = e as EntityAlive;
                if (alive == null || alive.IsDead()) continue;

                // Never scoop something a player is riding or that is attached to anything.
                if (e.AttachedToEntity != null || e.GetFirstAttached() != null) continue;

                string itemName = GetPickupItemName(e);
                if (string.IsNullOrEmpty(itemName)) continue;

                ItemValue iv = ItemClass.GetItem(itemName, false);
                if (iv == null || iv.IsEmpty()) continue;

                var stack = new ItemStack(iv.Clone(), 1);

                // Vehicle storage is a Bag, and Bag.AddItem gates on StackLocationTypes.Backpack
                // regardless of the fact that no backpack is involved. An item whose RestrictedMove
                // list leaves Backpack out can therefore never be stored - that is a config problem,
                // not a full truck, so say so in the log instead of blaming the storage.
                if (!stack.CanMoveTo(XUiC_ItemStack.StackLocationTypes.Backpack))
                {
                    if (WarnedRestricted.Add(itemName))
                    {
                        Log.Warning("[ChickenScoop] '" + itemName + "' cannot be put in vehicle storage: its "
                            + "RestrictedMove list has no Backpack entry, which Bag.AddItem requires. "
                            + "Add Backpack to that item's RestrictedMove property.");
                    }
                    continue;
                }

                if (!bag.CanTakeItem(stack) || !bag.AddItem(stack))
                {
                    full = true;
                    break;
                }

                v.SetBagModified();

                // Captured, not Killed: the bird is alive in the truck bed, not a corpse.
                world.RemoveEntity(e.entityId, EnumRemoveEntityReason.Captured);
                scooped++;
            }

            ScanBuffer.Clear();

            if (scooped > 0)
            {
                WarnedFull.Remove(v.entityId);

                if (ScoopConfig.PlaySound && GameManager.Instance != null)
                {
                    GameManager.Instance.PlaySoundAtPositionServer(
                        v.position, "chickenwild_grab", AudioRolloffMode.Logarithmic, 20, 1f);
                }

                if (ScoopConfig.NotifyDriver)
                {
                    NotifyDriver(v, scooped == 1
                        ? "Scooped a chicken into the truck."
                        : "Scooped " + scooped + " chickens into the truck.");
                }
            }
            else if (full && !WarnedFull.Contains(v.entityId))
            {
                WarnedFull.Add(v.entityId);
                if (ScoopConfig.NotifyDriver)
                {
                    NotifyDriver(v, "Vehicle storage is full - the chicken got away.");
                }
            }
        }

        /// <summary>Debug bail reasons, once per vehicle per reason, so the log stays readable.</summary>
        private static readonly HashSet<string> DebugSeen = new HashSet<string>();

        private static void DebugOnce(int entityId, string reason)
        {
            if (DebugSeen.Add(entityId + ":" + reason))
            {
                Log.Out("[ChickenScoop] veh " + entityId + " not scooping: " + reason);
            }
        }

        /// <summary>
        /// Reads the entity class's vanilla PickupItem property, so anything the game already lets
        /// you carry by hand (animalChicken -> wildChicken) is scoopable, including modded animals.
        /// </summary>
        private static string GetPickupItemName(Entity e)
        {
            EntityClass ec;
            if (!EntityClass.list.TryGetValue(e.entityClass, out ec)) return null;
            if (ec == null || ec.Properties == null) return null;
            if (!ec.Properties.Contains("PickupItem")) return null;
            return ec.Properties.GetString("PickupItem");
        }

        private static void NotifyDriver(EntityVehicle v, string message)
        {
            var driver = v.GetAttached(0) as EntityPlayer;
            if (driver == null) return;
            GameManager.ShowTooltipMP(driver, message, null);
        }
    }
}
