/*
 * Chaotic's Reach - craft using items in nearby containers.
 *
 * Three patches on XUiM_PlayerInventory, which is what the crafting window asks
 * "do I have this / take this from me":
 *
 *   GetItemCount  postfix  - so the recipe shows the real total and stops being greyed out
 *   HasItems      postfix  - so a recipe you can only afford via storage is craftable
 *   RemoveItems   prefix   - pulls the shortfall into the backpack first, then lets
 *                            vanilla do the actual removal, so all the accounting
 *                            stays in vanilla's hands and can't double-spend.
 *
 * Only containers the player could open themselves are considered: player-placed
 * storage, and never one that is locked by somebody else.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

public class ChaoticReachInit : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        ChaoticReach.LoadConfig(_modInstance.Path);
        new Harmony("com.chaotic.reach").PatchAll(Assembly.GetExecutingAssembly());
        Log.Out("[ChaoticReach] loaded. enabled={0} radius={1}", ChaoticReach.Enabled, ChaoticReach.Radius);
    }
}

public static class ChaoticReach
{
    public static bool Enabled = true;
    public static float Radius = 20f;
    public static bool Debug = false;

    // Re-scanning every chunk for every UI refresh would be brutal; the crafting
    // window calls GetItemCount many times per frame. One scan per 500ms is plenty.
    private static readonly List<ITileEntityLootable> cache = new List<ITileEntityLootable>();
    private static float lastScan = -999f;
    private static Vector3 lastScanPos = Vector3.zero;

    public static void LoadConfig(string modPath)
    {
        string file = Path.Combine(modPath, "ChaoticReach.cfg");
        if (!File.Exists(file)) return;
        foreach (string raw in File.ReadAllLines(file))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq).Trim();
            string val = line.Substring(eq + 1).Trim();
            if (key.EqualsCaseInsensitive("Enabled")) bool.TryParse(val, out Enabled);
            else if (key.EqualsCaseInsensitive("Radius")) float.TryParse(val, out Radius);
            else if (key.EqualsCaseInsensitive("Debug")) bool.TryParse(val, out Debug);
        }
        if (Radius < 1f) Radius = 1f;
        if (Radius > 100f) Radius = 100f;
    }

    // Window groups that mean "the player is looking at a crafting list".
    // Names taken from Data/Config/XUi_InGame/windows.xml.
    private static readonly string[] CraftWindows =
    {
        "crafting", "workstation_workbench", "workstation_forge", "workstation_forge_nosmelting",
        "workstation_campfire", "workstation_chemistryStation", "workstation_cementMixer",
    };

    /// <summary>
    /// True only while a crafting list is open. GetItemCount is a general
    /// purpose call, so inflating it everywhere would tell the player they are
    /// carrying things that are actually in a chest. Restricting it to the
    /// crafting UI keeps the lie where it is useful and true.
    /// </summary>
    public static bool CraftingWindowOpen(XUi xui)
    {
        if (xui == null) return false;
        for (int i = 0; i < CraftWindows.Length; i++)
        {
            XUiController c = xui.FindWindowGroupByName(CraftWindows[i]);
            if (c != null && c.WindowGroup != null && c.WindowGroup.isShowing) return true;
        }
        return false;
    }

    public static EntityPlayerLocal LocalPlayer()
    {
        World w = GameManager.Instance != null ? GameManager.Instance.World : null;
        return w != null ? w.GetPrimaryPlayer() : null;
    }

    /// <summary>Player-openable storage within Radius of the player.</summary>
    public static List<ITileEntityLootable> Containers()
    {
        cache.Clear();
        if (!Enabled) return cache;

        EntityPlayerLocal player = LocalPlayer();
        World world = GameManager.Instance != null ? GameManager.Instance.World : null;
        if (player == null || world == null) return cache;

        Vector3 pos = player.GetPosition();
        float r2 = Radius * Radius;

        List<Chunk> chunks = world.ChunkCache.GetChunkArrayCopySync();
        foreach (Chunk chunk in chunks)
        {
            if (chunk == null) continue;
            DictionaryList<Vector3i, TileEntity> tes = chunk.GetTileEntities();
            for (int i = 0; i < tes.list.Count; i++)
            {
                TileEntity te = tes.list[i];
                if (te == null) continue;

                ITileEntityLootable loot;
                if (!te.TryGetSelfOrFeature(out loot)) continue;
                if (!loot.bPlayerStorage) continue;          // never craft out of un-looted world loot
                if (loot.items == null) continue;

                Vector3i wp = te.ToWorldPos();
                if ((new Vector3(wp.x, wp.y, wp.z) - pos).sqrMagnitude > r2) continue;

                ILockable lockable;
                if (te.TryGetSelfOrFeature(out lockable))
                {
                    if (lockable.IsLocked() && !lockable.LocalPlayerIsOwner()) continue;
                }

                cache.Add(loot);
            }
        }

        if (Debug) Log.Out("[ChaoticReach] {0} container(s) in range", cache.Count);
        lastScan = Time.time;
        lastScanPos = pos;
        return cache;
    }

    /// <summary>Cached wrapper - the crafting UI hits this dozens of times a frame.</summary>
    public static List<ITileEntityLootable> ContainersCached()
    {
        EntityPlayerLocal player = LocalPlayer();
        if (player == null) { cache.Clear(); return cache; }
        if (Time.time - lastScan < 0.5f && (player.GetPosition() - lastScanPos).sqrMagnitude < 4f) return cache;
        return Containers();
    }

    public static int CountInContainers(ItemValue iv)
    {
        int total = 0;
        List<ITileEntityLootable> list = ContainersCached();
        for (int c = 0; c < list.Count; c++)
        {
            ItemStack[] items = list[c].items;
            if (items == null) continue;
            for (int i = 0; i < items.Length; i++)
            {
                ItemStack s = items[i];
                if (s == null || s.IsEmpty()) continue;
                if (s.itemValue.type == iv.type) total += s.count;
            }
        }
        return total;
    }

    /// <summary>Move up to <paramref name="need"/> of an item from storage into the backpack.</summary>
    public static int PullIntoInventory(XUiM_PlayerInventory inv, ItemValue iv, int need)
    {
        if (need <= 0) return 0;
        int moved = 0;
        List<ITileEntityLootable> list = ContainersCached();

        for (int c = 0; c < list.Count && moved < need; c++)
        {
            ITileEntityLootable box = list[c];
            int want = need - moved;
            int got = box.RemoveItems(iv, want);
            if (got <= 0) continue;

            box.SetModified();

            ItemStack stack = new ItemStack(iv.Clone(), got);
            if (!inv.AddItem(stack, false))
            {
                // Backpack is full. Never destroy what we took - put it on the ground.
                GameManager.Instance.ItemDropServer(stack, LocalPlayer().GetPosition(), Vector3.zero);
            }
            moved += got;
        }

        if (Debug && moved > 0) Log.Out("[ChaoticReach] pulled {0}x{1} from storage", moved, iv.ItemClass?.GetItemName());
        return moved;
    }
}

[HarmonyPatch(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.GetItemCount), new Type[] { typeof(ItemValue) })]
public static class Patch_GetItemCount
{
    private static void Postfix(XUiM_PlayerInventory __instance, ItemValue _itemValue, ref int __result)
    {
        if (!ChaoticReach.Enabled || _itemValue == null) return;
        if (!ChaoticReach.CraftingWindowOpen(__instance.xui)) return;
        __result += ChaoticReach.CountInContainers(_itemValue);
    }
}

[HarmonyPatch(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.HasItems))]
public static class Patch_HasItems
{
    private static void Postfix(XUiM_PlayerInventory __instance, IList<ItemStack> _itemStacks, int _multiplier, ref bool __result)
    {
        if (__result || !ChaoticReach.Enabled || _itemStacks == null) return;

        for (int i = 0; i < _itemStacks.Count; i++)
        {
            int need = _itemStacks[i].count * _multiplier;
            need -= __instance.GetItemCount(_itemStacks[i].itemValue); // already includes storage
            if (need > 0) return;
        }
        __result = true;
    }
}

[HarmonyPatch(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.RemoveItems))]
public static class Patch_RemoveItems
{
    private static void Prefix(XUiM_PlayerInventory __instance, IList<ItemStack> _itemStacks, int _multiplier)
    {
        if (!ChaoticReach.Enabled || _itemStacks == null) return;

        for (int i = 0; i < _itemStacks.Count; i++)
        {
            ItemValue iv = _itemStacks[i].itemValue;
            int need = _itemStacks[i].count * _multiplier;

            // Vanilla only ever looks at backpack + toolbelt, so top those up first
            // and then get out of the way.
            int onPerson = __instance.Backpack.GetItemCount(iv) + __instance.Toolbelt.GetItemCount(iv);
            int shortfall = need - onPerson;
            if (shortfall > 0) ChaoticReach.PullIntoInventory(__instance, iv, shortfall);
        }
    }
}