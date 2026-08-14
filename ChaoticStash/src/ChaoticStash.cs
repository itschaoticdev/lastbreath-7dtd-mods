/*
 * Chaotic's Stash - one key, and everything you are carrying that a nearby
 * container ALREADY holds goes home.
 *
 * Why this is not quick stack:
 *   Vanilla's "smart" move (XUiM_LootContainer.StashItems with FillAndCreate)
 *   does the right thing, but only into the ONE container you have open, and
 *   only while its window is up. This does the same rule - top up matching
 *   stacks first, and only start a new stack in a container that already has
 *   that item - across every container in range, with no window open at all.
 *
 * Nothing is ever moved into a container that does not already hold the item,
 * so a chest never gains a category it did not already have.
 *
 * Containers considered: player-placed storage only, never an un-looted world
 * crate, never one locked by somebody else, never one someone has open.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

public class ChaoticStashInit : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        ChaoticStash.LoadConfig(_modInstance.Path);
        new Harmony("com.chaotic.stash").PatchAll(Assembly.GetExecutingAssembly());
        Log.Out("[ChaoticStash] loaded. enabled={0} key={1} radius={2} toolbelt={3} auto={4}",
            ChaoticStash.Enabled, ChaoticStash.Key, ChaoticStash.Radius,
            ChaoticStash.IncludeToolbelt, ChaoticStash.AutoStash);
    }
}

public static class ChaoticStash
{
    public static bool Enabled = true;
    public static KeyCode Key = KeyCode.V;
    public static float Radius = 25f;
    public static bool IncludeToolbelt = false;
    public static bool IncludeQualityItems = false;
    public static bool RespectLockedSlots = true;
    public static bool CreateNewStacks = true;
    public static bool OnlyPlayerStorage = true;
    public static bool RequireLandClaim = false;
    public static bool AutoStash = false;
    public static float AutoStashInterval = 5f;
    public static bool ShowMessage = true;
    public static bool Debug = false;

    /// <summary>Item names that never leave the player, whatever a chest holds.</summary>
    public static readonly HashSet<string> Ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static float lastAuto = -999f;

    public struct Target
    {
        public TileEntity te;
        public ITileEntityLootable loot;
    }

    private static readonly List<Target> targets = new List<Target>();

    // ---------------------------------------------------------------- config

    public static void LoadConfig(string modPath)
    {
        string file = Path.Combine(modPath, "ChaoticStash.cfg");
        if (!File.Exists(file))
        {
            Log.Warning("[ChaoticStash] no ChaoticStash.cfg next to the dll, using defaults");
            return;
        }

        foreach (string raw in File.ReadAllLines(file))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq).Trim();
            string val = line.Substring(eq + 1).Trim();

            if (key.EqualsCaseInsensitive("Enabled")) bool.TryParse(val, out Enabled);
            else if (key.EqualsCaseInsensitive("Key")) ParseKey(val);
            else if (key.EqualsCaseInsensitive("Radius")) float.TryParse(val, out Radius);
            else if (key.EqualsCaseInsensitive("IncludeToolbelt")) bool.TryParse(val, out IncludeToolbelt);
            else if (key.EqualsCaseInsensitive("IncludeQualityItems")) bool.TryParse(val, out IncludeQualityItems);
            else if (key.EqualsCaseInsensitive("RespectLockedSlots")) bool.TryParse(val, out RespectLockedSlots);
            else if (key.EqualsCaseInsensitive("CreateNewStacks")) bool.TryParse(val, out CreateNewStacks);
            else if (key.EqualsCaseInsensitive("OnlyPlayerStorage")) bool.TryParse(val, out OnlyPlayerStorage);
            else if (key.EqualsCaseInsensitive("RequireLandClaim")) bool.TryParse(val, out RequireLandClaim);
            else if (key.EqualsCaseInsensitive("AutoStash")) bool.TryParse(val, out AutoStash);
            else if (key.EqualsCaseInsensitive("AutoStashInterval")) float.TryParse(val, out AutoStashInterval);
            else if (key.EqualsCaseInsensitive("ShowMessage")) bool.TryParse(val, out ShowMessage);
            else if (key.EqualsCaseInsensitive("Debug")) bool.TryParse(val, out Debug);
            else if (key.EqualsCaseInsensitive("IgnoreItems"))
            {
                Ignore.Clear();
                foreach (string n in val.Split(','))
                {
                    string t = n.Trim();
                    if (t.Length > 0) Ignore.Add(t);
                }
            }
        }

        if (Radius < 1f) Radius = 1f;
        if (Radius > 100f) Radius = 100f;
        if (AutoStashInterval < 1f) AutoStashInterval = 1f;
    }

    private static void ParseKey(string val)
    {
        try
        {
            Key = (KeyCode)Enum.Parse(typeof(KeyCode), val, true);
        }
        catch (Exception)
        {
            Log.Warning("[ChaoticStash] '{0}' is not a UnityEngine.KeyCode name, keeping {1}", val, Key);
        }
    }

    // ------------------------------------------------------------- the hotkey

    public static void Tick(EntityPlayerLocal player)
    {
        if (!Enabled || player == null || player.IsDead()) return;

        LocalPlayerUI ui = player.PlayerUI;
        if (ui == null || ui.windowManager == null) return;

        // Never eat the key while the player is typing in chat, a search box or the console.
        if (ui.windowManager.IsInputActive()) return;

        if (Input.GetKeyDown(Key))
        {
            Stash(player, false);
            return;
        }

        if (AutoStash && Time.time - lastAuto >= AutoStashInterval)
        {
            lastAuto = Time.time;
            Stash(player, true);
        }
    }

    // -------------------------------------------------------------- the work

    public static void Stash(EntityPlayerLocal player, bool quiet)
    {
        World world = GameManager.Instance != null ? GameManager.Instance.World : null;
        if (world == null) return;

        FindContainers(player, world);
        if (targets.Count == 0)
        {
            if (!quiet && ShowMessage) Msg(player, "Nothing to stash into - no storage of yours in range.");
            return;
        }

        int movedItems = 0;
        int movedStacks = 0;
        HashSet<Vector3i> touched = new HashSet<Vector3i>();

        // --- backpack -------------------------------------------------------
        Bag bag = player.bag;
        if (bag != null)
        {
            ItemStack[] slots = bag.GetSlots();
            PackedBoolArray locked = bag.LockedSlots;

            for (int i = 0; i < slots.Length; i++)
            {
                ItemStack src = slots[i];
                if (src == null || src.IsEmpty()) continue;
                if (RespectLockedSlots && locked != null && i < locked.Length && locked[i]) continue;
                if (!Stashable(src)) continue;

                ItemStack work = src.Clone();
                int before = work.count;
                Deposit(work, touched);
                if (work.count == before) continue;

                movedItems += before - work.count;
                movedStacks++;
                bag.SetSlot(i, work.count > 0 ? work : ItemStack.Empty);
            }
            bag.onBackpackChanged();
        }

        // --- toolbelt (opt-in, never the item in your hands) -----------------
        if (IncludeToolbelt && player.inventory != null)
        {
            Inventory inv = player.inventory;
            int held = inv.m_HoldingItemIdx;
            int count = inv.PUBLIC_SLOTS;

            for (int i = 0; i < count; i++)
            {
                if (i == held) continue;
                ItemStack src = inv.GetItem(i);
                if (src == null || src.IsEmpty()) continue;
                if (!Stashable(src)) continue;

                ItemStack work = src.Clone();
                int before = work.count;
                Deposit(work, touched);
                if (work.count == before) continue;

                movedItems += before - work.count;
                movedStacks++;
                inv.SetItem(i, work.count > 0 ? work : ItemStack.Empty);
            }
            inv.CallOnToolbeltChangedInternal();
        }

        if (!ShowMessage) return;
        if (movedItems > 0)
        {
            Msg(player, string.Format("Stashed {0} item{1} from {2} stack{3} into {4} container{5}.",
                movedItems, movedItems == 1 ? "" : "s",
                movedStacks, movedStacks == 1 ? "" : "s",
                touched.Count, touched.Count == 1 ? "" : "s"));
        }
        else if (!quiet)
        {
            Msg(player, "Nothing you are carrying is already in a container nearby.");
        }
    }

    /// <summary>
    /// Vanilla's rule, applied to every container instead of one: fill matching
    /// stacks first, then a fresh slot but only in a container that already
    /// holds this item.
    /// </summary>
    private static void Deposit(ItemStack work, HashSet<Vector3i> touched)
    {
        for (int i = 0; i < targets.Count && work.count > 0; i++)
        {
            Target t = targets[i];
            ITileEntityLootable box = t.loot;
            if (box == null || box.items == null) continue;
            if (!box.HasItem(work.itemValue)) continue;   // "already has the item" - the whole point

            int before = work.count;

            box.TryStackItem(0, work);

            if (work.count > 0 && CreateNewStacks && box.AddItem(work.Clone()))
            {
                work.count = 0;
            }

            if (work.count != before)
            {
                box.SetModified();
                touched.Add(t.te.ToWorldPos());
            }
        }
    }

    private static bool Stashable(ItemStack stack)
    {
        ItemValue iv = stack.itemValue;
        if (iv == null || iv.type == 0) return false;

        ItemClass ic = iv.ItemClass;
        if (ic == null) return false;

        // Guns, tools and armour carry a quality and never merge - moving them
        // because a chest happens to hold one of the same kind is a nasty surprise.
        if (!IncludeQualityItems && iv.HasQuality) return false;

        if (Ignore.Count > 0 && Ignore.Contains(ic.GetItemName())) return false;

        // Items the game itself forbids from a container (held entities and such).
        if (!stack.CanMoveTo(XUiC_ItemStack.StackLocationTypes.LootContainer)) return false;

        return true;
    }

    private static void FindContainers(EntityPlayerLocal player, World world)
    {
        targets.Clear();

        Vector3 pos = player.GetPosition();
        float r2 = Radius * Radius;
        PersistentPlayerData ppd = world.GetGameManager().GetPersistentLocalPlayer();

        List<Chunk> chunks = world.ChunkCache.GetChunkArrayCopySync();
        for (int c = 0; c < chunks.Count; c++)
        {
            Chunk chunk = chunks[c];
            if (chunk == null) continue;

            DictionaryList<Vector3i, TileEntity> tes = chunk.GetTileEntities();
            for (int i = 0; i < tes.list.Count; i++)
            {
                TileEntity te = tes.list[i];
                if (te == null) continue;

                ITileEntityLootable loot;
                if (!te.TryGetSelfOrFeature(out loot)) continue;
                if (loot.items == null) continue;

                // Never an un-looted world crate - that is loot, not storage.
                if (OnlyPlayerStorage && !loot.bPlayerStorage) continue;

                Vector3i wp = te.ToWorldPos();
                if ((new Vector3(wp.x, wp.y, wp.z) - pos).sqrMagnitude > r2) continue;

                // Somebody is looking inside it right now - their UI would desync.
                if (te.IsUserAccessing()) continue;

                ILockable lockable;
                if (te.TryGetSelfOrFeature(out lockable))
                {
                    if (lockable.IsLocked() && !lockable.LocalPlayerIsOwner()) continue;
                }

                if (RequireLandClaim && !world.IsMyLandProtectedBlock(wp, ppd)) continue;

                targets.Add(new Target { te = te, loot = loot });
            }
        }

        if (Debug) Log.Out("[ChaoticStash] {0} container(s) in range", targets.Count);
    }

    private static void Msg(EntityPlayerLocal player, string text)
    {
        GameManager.ShowTooltip(player, "Chaotic's Stash: " + text);
    }
}

[HarmonyPatch(typeof(EntityPlayerLocal), "Update")]
public static class Patch_EntityPlayerLocal_Update
{
    private static void Postfix(EntityPlayerLocal __instance)
    {
        try
        {
            ChaoticStash.Tick(__instance);
        }
        catch (Exception e)
        {
            // A throw here would run once a frame forever. Say it once and disable.
            Log.Error("[ChaoticStash] disabled after error: " + e);
            ChaoticStash.Enabled = false;
        }
    }
}