/*
 * Chaotic's Pickup - take the workstations and the furniture with you.
 *
 * Two separate things stop you picking something up in vanilla V3, and this
 * mod addresses both:
 *
 *  1. Block.CanPickup is false, so there is no "take" option at all. Rather
 *     than shipping a giant xpath list that rots on every game update, the
 *     mod flips the flag on the blocks that match a rule set, in a postfix on
 *     BlocksFromXml.InitBlock - after the block has parsed its own properties.
 *     Modded blocks get the same treatment for free.
 *
 *  2. The block DOES have a take command, but every class that owns one
 *     (BlockWorkstation, BlockPowered and friends) gates it on
 *         IsMyLandProtectedBlock(pos, me) && TakeDelay > 0
 *     which is why a forge standing in a POI can never be taken. The postfix
 *     re-enables that command using CanPickupBlockAt instead, which still
 *     refuses anything inside SOMEONE ELSE'S claim. BlockWorkstation also
 *     wants IsPlayerPlaced, and that requirement is simply dropped.
 *
 * Two safety rules, because the fun version of this mod is an item printer:
 *   - an un-looted world loot container can never be pocketed (pick it up,
 *     re-place it, and it would roll fresh loot),
 *   - a container or workstation with anything inside it must be emptied
 *     first, unless AllowNonEmpty is on, in which case the contents are
 *     handed to you rather than deleted.
 *
 * A block that is damaged still has to be repaired first. That is vanilla and
 * it is left alone.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

public class ChaoticPickupInit : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        ChaoticPickup.LoadConfig(_modInstance.Path);
        Harmony harmony = new Harmony("com.chaotic.pickup");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        ChaoticPickup.PatchTakeCommandOwners(harmony);
        Log.Out("[ChaoticPickup] loaded. enabled={0} nonEmpty={1} classes={2} tags={3}",
            ChaoticPickup.Enabled, ChaoticPickup.AllowNonEmpty,
            ChaoticPickup.PickupClasses.Count, ChaoticPickup.AllowTags.Count);
    }
}

public static class ChaoticPickup
{
    public static bool Enabled = true;
    public static bool AllowNonEmpty = false;
    public static bool AllowWorldLootContainers = false;
    public static bool RespectDisabledTakeDelay = true;
    public static bool AllowMultiBlock = true;
    public static bool Debug = false;

    /// <summary>
    /// Classes that already own a "take" command. They are re-enabled by the
    /// command patch below, so the CanPickup flip must leave them alone -
    /// their own OnBlockActivated does something else entirely (open the UI).
    /// </summary>
    public static readonly string[] TakeOwnerClasses =
    {
        "BlockWorkstation", "BlockPowered", "BlockPowerSource", "BlockPoweredLight",
        "BlockPoweredDoor", "BlockSpotlight", "BlockSwitch", "BlockPressurePlate",
        "BlockTripWire", "BlockMotionSensor", "BlockTimerRelay", "BlockRanged",
        "BlockLauncher", "BlockCollector", "BlockInfo",
    };

    public static readonly HashSet<string> PickupClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public static readonly HashSet<string> AllowTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public static readonly HashSet<string> DenyTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public static readonly HashSet<string> AllowBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public static readonly HashSet<string> DenyBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public static readonly List<string> DenyNameContains = new List<string>();

    /// <summary>Blocks this mod made pickable, so the safety checks only police our own doing.</summary>
    private static readonly HashSet<string> ourBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static int flipped;

    // ---------------------------------------------------------------- config

    private const string DefaultClasses = "BlockLight,BlockLadder,BlockSleepingBag,BlockCampfire";

    private const string DefaultAllowTags =
        "SC_decor,SC_electrical,SC_lighting,SC_plumbing,SC_commercial,SC_residential," +
        "SC_industrial,SC_automotive,SC_traps,SC_signs,MC_playerBlocks,SC_playerHelpers";

    // MC_building is deliberately NOT here. It is the top level creative menu
    // category and sits on every couch, bed, sink and toilet in the game -
    // denying it would gut the whole point. Plain building materials carry no
    // SC_* tag at all, so the allow list already excludes them.
    private const string DefaultDenyTags =
        "MC_Shapes,MC_helpers,SC_terrain,SC_trees,SC_shrubbery,SC_crops," +
        "SC_questblocks,SC_treasureRoom,SC_destruction,SC_helperSleeper";

    private const string DefaultDenyNameContains = "vending,trader,questBlock,supplyCrate,airdrop";

    private const string DefaultDenyBlocks = "keystoneBlock";

    public static void LoadConfig(string modPath)
    {
        Fill(PickupClasses, DefaultClasses);
        Fill(AllowTags, DefaultAllowTags);
        Fill(DenyTags, DefaultDenyTags);
        Fill(DenyBlocks, DefaultDenyBlocks);
        FillList(DenyNameContains, DefaultDenyNameContains);

        string file = Path.Combine(modPath, "ChaoticPickup.cfg");
        if (!File.Exists(file))
        {
            Log.Warning("[ChaoticPickup] no ChaoticPickup.cfg next to the dll, using defaults");
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
            else if (key.EqualsCaseInsensitive("AllowNonEmpty")) bool.TryParse(val, out AllowNonEmpty);
            else if (key.EqualsCaseInsensitive("AllowWorldLootContainers")) bool.TryParse(val, out AllowWorldLootContainers);
            else if (key.EqualsCaseInsensitive("RespectDisabledTakeDelay")) bool.TryParse(val, out RespectDisabledTakeDelay);
            else if (key.EqualsCaseInsensitive("AllowMultiBlock")) bool.TryParse(val, out AllowMultiBlock);
            else if (key.EqualsCaseInsensitive("Debug")) bool.TryParse(val, out Debug);
            else if (key.EqualsCaseInsensitive("PickupClasses")) Fill(PickupClasses, val);
            else if (key.EqualsCaseInsensitive("AllowFilterTags")) Fill(AllowTags, val);
            else if (key.EqualsCaseInsensitive("DenyFilterTags")) Fill(DenyTags, val);
            else if (key.EqualsCaseInsensitive("AllowBlocks")) Fill(AllowBlocks, val);
            else if (key.EqualsCaseInsensitive("DenyBlocks")) Fill(DenyBlocks, val);
            else if (key.EqualsCaseInsensitive("DenyNameContains")) FillList(DenyNameContains, val);
        }
    }

    private static void Fill(HashSet<string> set, string csv)
    {
        set.Clear();
        foreach (string s in csv.Split(','))
        {
            string t = s.Trim();
            if (t.Length > 0) set.Add(t);
        }
    }

    private static void FillList(List<string> list, string csv)
    {
        list.Clear();
        foreach (string s in csv.Split(','))
        {
            string t = s.Trim();
            if (t.Length > 0) list.Add(t.ToLowerInvariant());
        }
    }

    // ------------------------------------------------- rule 1: flip CanPickup

    public static void ApplyRules(Block block)
    {
        if (!Enabled || block == null) return;

        string name = block.GetBlockName();
        if (string.IsNullOrEmpty(name)) return;
        if (block.CanPickup) return;                       // vanilla already allows it

        // These have their own take command and their own OnBlockActivated -
        // the command patch handles them, flipping the flag here would do
        // nothing useful and could shadow the real activation.
        if (IsTakeOwner(block)) return;

        // A child of a multi-block would be removed on its own and orphan the
        // rest. Vanilla only ever ships CanPickup on single blocks.
        if (!AllowMultiBlock && block.isMultiBlock) return;

        if (DenyBlocks.Contains(name)) return;

        if (!AllowBlocks.Contains(name))
        {
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < DenyNameContains.Count; i++)
            {
                if (lower.Contains(DenyNameContains[i])) return;
            }

            if (!ClassAllowed(block) && !TagsAllowed(block)) return;
        }

        block.CanPickup = true;
        ourBlocks.Add(name);
        flipped++;
        if (Debug) Log.Out("[ChaoticPickup] pickable: {0}", name);
    }

    private static bool IsTakeOwner(Block block)
    {
        for (Type t = block.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            for (int i = 0; i < TakeOwnerClasses.Length; i++)
            {
                if (t.Name == TakeOwnerClasses[i]) return true;
            }
        }
        return false;
    }

    private static bool ClassAllowed(Block block)
    {
        // Walk the type chain so BlockPoweredLight matches a "BlockPowered" rule.
        for (Type t = block.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            if (PickupClasses.Contains(t.Name)) return true;
        }
        return false;
    }

    private static bool TagsAllowed(Block block)
    {
        string tags = block.Properties != null ? block.Properties.GetString("FilterTags") : null;
        if (string.IsNullOrEmpty(tags)) return false;

        bool allowed = false;
        foreach (string raw in tags.Split(','))
        {
            string tag = raw.Trim();
            if (tag.Length == 0) continue;
            if (DenyTags.Contains(tag)) return false;                 // deny always wins
            if (tag.StartsWith("SC_helper", StringComparison.OrdinalIgnoreCase)) return false;
            if (AllowTags.Contains(tag)) allowed = true;
        }
        return allowed;
    }

    public static bool IsOurs(Block block)
    {
        return block != null && ourBlocks.Contains(block.GetBlockName());
    }

    public static void LogSummary()
    {
        Log.Out("[ChaoticPickup] made {0} extra block(s) pickable", flipped);
    }

    // ------------------------------- rule 2: re-enable the "take" command

    private static readonly Dictionary<Type, FieldInfo> takeDelayFields = new Dictionary<Type, FieldInfo>();

    /// <summary>
    /// Every class that declares its own "take" activation command gates it on
    /// "is this inside MY claim". Patch each of them by name so the derived
    /// overrides are covered too - a Harmony patch on the base would not be.
    /// </summary>
    public static void PatchTakeCommandOwners(Harmony harmony)
    {
        if (!Enabled) return;

        string[] typeNames = TakeOwnerClasses;

        MethodInfo postfix = AccessTools.Method(typeof(Patch_TakeCommands), nameof(Patch_TakeCommands.Postfix));

        foreach (string tn in typeNames)
        {
            Type t = AccessTools.TypeByName(tn);
            if (t == null) continue;

            MethodInfo m = AccessTools.DeclaredMethod(t, "GetBlockActivationCommands");
            if (m == null) continue;                      // class inherits it, base patch covers it

            try
            {
                harmony.Patch(m, null, new HarmonyMethod(postfix));
                if (Debug) Log.Out("[ChaoticPickup] patched {0}.GetBlockActivationCommands", tn);
            }
            catch (Exception e)
            {
                Log.Warning("[ChaoticPickup] could not patch {0}: {1}", tn, e.Message);
            }
        }
    }

    public static float GetTakeDelay(Block block)
    {
        Type t = block.GetType();
        FieldInfo fi;
        if (!takeDelayFields.TryGetValue(t, out fi))
        {
            fi = AccessTools.Field(t, "TakeDelay");
            takeDelayFields[t] = fi;
        }
        if (fi == null) return 1f;                        // no field - nothing to respect
        try { return (float)fi.GetValue(block); }
        catch (Exception) { return 1f; }
    }

    public static void EnableTake(Block block, BlockActivationCommand[] cmds, WorldBase world, Vector3i pos)
    {
        if (!Enabled || cmds == null || world == null) return;

        float delay = GetTakeDelay(block);
        if (RespectDisabledTakeDelay && delay <= 0f) return;   // TFP switched this one off on purpose

        // Still refuses anything standing inside another player's claim.
        if (!world.CanPickupBlockAt(pos, world.GetGameManager().GetPersistentLocalPlayer())) return;

        for (int i = 0; i < cmds.Length; i++)
        {
            if (cmds[i].text == "take")
            {
                cmds[i].enabled = true;
                return;
            }
        }
    }

    // ------------------------------------------------------ the safety rules

    /// <summary>
    /// Returns false and tells the player why, when taking this block would
    /// either delete items or print loot.
    /// </summary>
    public static bool SafeToTake(Vector3i pos, EntityPlayerLocal player)
    {
        World world = GameManager.Instance != null ? GameManager.Instance.World : null;
        if (world == null) return true;

        TileEntity te = world.GetTileEntity(pos);
        if (te == null) return true;

        ITileEntityLootable loot;
        if (te.TryGetSelfOrFeature(out loot))
        {
            if (!AllowWorldLootContainers && !loot.bPlayerStorage)
            {
                Tip(player, "This one holds world loot - it stays where it is.");
                return false;
            }
            if (!loot.IsEmpty())
            {
                if (!AllowNonEmpty)
                {
                    Tip(player, "Empty it first.");
                    return false;
                }
                DrainLootable(loot, player);
            }
        }

        TileEntityWorkstation ws = te as TileEntityWorkstation;
        if (ws != null && !ws.IsEmpty)
        {
            if (!AllowNonEmpty)
            {
                Tip(player, "Empty it first.");
                return false;
            }
            DrainWorkstation(ws, player);
        }

        return true;
    }

    private static void DrainLootable(ITileEntityLootable loot, EntityPlayerLocal player)
    {
        ItemStack[] items = loot.items;
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] == null || items[i].IsEmpty()) continue;
            Give(player, items[i].Clone());
            loot.UpdateSlot(i, ItemStack.Empty);
        }
        loot.SetModified();
    }

    private static void DrainWorkstation(TileEntityWorkstation ws, EntityPlayerLocal player)
    {
        DrainArray(ws.Input, player);
        DrainArray(ws.Output, player);
        DrainArray(ws.Fuel, player);
        DrainArray(ws.Tools, player);
        ws.SetModified();
    }

    private static void DrainArray(ItemStack[] arr, EntityPlayerLocal player)
    {
        if (arr == null) return;
        for (int i = 0; i < arr.Length; i++)
        {
            if (arr[i] == null || arr[i].IsEmpty()) continue;
            Give(player, arr[i].Clone());
            arr[i] = ItemStack.Empty;
        }
    }

    /// <summary>Into the backpack, and onto the ground rather than into the void.</summary>
    private static void Give(EntityPlayerLocal player, ItemStack stack)
    {
        LocalPlayerUI ui = LocalPlayerUI.GetUIForPlayer(player);
        if (ui != null && ui.xui != null && ui.xui.PlayerInventory != null)
        {
            if (ui.xui.PlayerInventory.AddItem(stack)) return;
            ui.xui.PlayerInventory.DropItem(stack);
            return;
        }
        GameManager.Instance.ItemDropServer(stack, player.GetPosition(), Vector3.zero);
    }

    private static void Tip(EntityPlayerLocal player, string text)
    {
        if (player == null) return;
        GameManager.ShowTooltip(player, "Chaotic's Pickup: " + text, string.Empty, "ui_denied");
    }
}

// ---------------------------------------------------------------- patches

[HarmonyPatch(typeof(BlocksFromXml), nameof(BlocksFromXml.InitBlock))]
public static class Patch_InitBlock
{
    private static void Postfix(Block block)
    {
        try { ChaoticPickup.ApplyRules(block); }
        catch (Exception e) { Log.Error("[ChaoticPickup] ApplyRules: " + e); }
    }
}

/// <summary>Applied by hand to every class that declares its own take command.</summary>
public static class Patch_TakeCommands
{
    public static void Postfix(Block __instance, BlockActivationCommand[] __result,
                               WorldBase _world, Vector3i _blockPos)
    {
        try { ChaoticPickup.EnableTake(__instance, __result, _world, _blockPos); }
        catch (Exception e) { Log.Error("[ChaoticPickup] EnableTake: " + e); }
    }
}

/// <summary>The instant-pickup path, used by every plain block we flipped.</summary>
[HarmonyPatch(typeof(Block), nameof(Block.OnBlockActivated), new Type[]
    { typeof(WorldBase), typeof(Vector3i), typeof(BlockValue), typeof(EntityPlayerLocal) })]
public static class Patch_OnBlockActivated
{
    private static bool Prefix(Block __instance, WorldBase _world, Vector3i _blockPos,
                               BlockValue _blockValue, EntityPlayerLocal _player, ref bool __result)
    {
        try
        {
            if (!ChaoticPickup.Enabled || !ChaoticPickup.IsOurs(__instance)) return true;

            // Pressing E on a cell of a multi-block must take the whole thing,
            // not pocket one child and leave the rest standing.
            if (__instance.isMultiBlock && _blockValue.ischild && __instance.multiBlockPos != null)
            {
                Vector3i parent = __instance.multiBlockPos.GetParentPos(_blockPos, _blockValue);
                BlockValue pbv = _world.GetBlock(parent);
                if (!pbv.ischild && pbv.type == _blockValue.type)
                {
                    __result = __instance.OnBlockActivated(_world, parent, pbv, _player);
                    return false;
                }
            }

            if (ChaoticPickup.SafeToTake(_blockPos, _player)) return true;
            __result = false;
            return false;
        }
        catch (Exception e)
        {
            Log.Error("[ChaoticPickup] OnBlockActivated: " + e);
            return true;
        }
    }
}

/// <summary>The hold-to-take path, used by workstations and everything powered.</summary>
[HarmonyPatch(typeof(Block), nameof(Block.TakeItemWithTimerDone))]
public static class Patch_TakeItemWithTimerDone
{
    private static bool Prefix(TimerEventData _timerData)
    {
        try
        {
            if (!ChaoticPickup.Enabled) return true;
            object[] data = _timerData.Data as object[];
            if (data == null || data.Length < 3) return true;

            Vector3i pos = (Vector3i)data[1];
            EntityPlayerLocal player = data[2] as EntityPlayerLocal;
            return ChaoticPickup.SafeToTake(pos, player);
        }
        catch (Exception e)
        {
            Log.Error("[ChaoticPickup] TakeItemWithTimerDone: " + e);
            return true;
        }
    }
}

/// <summary>
/// Vanilla refuses a workstation that is not empty. When AllowNonEmpty is on
/// the contents are handed over in the timer-done prefix above instead.
/// </summary>
[HarmonyPatch(typeof(BlockWorkstation), "takeItemWithTimerCanTake")]
public static class Patch_WorkstationCanTake
{
    private static bool Prefix(ref bool __result)
    {
        if (!ChaoticPickup.Enabled || !ChaoticPickup.AllowNonEmpty) return true;
        __result = true;
        return false;
    }
}

/// <summary>
/// A loot container a player puts down becomes that player's storage, and is
/// emptied. Without this, carrying a POI fridge home and re-placing it would
/// give it a fresh roll of loot every time - an item printer. Vanilla does the
/// same thing for dew collectors and apiaries in BlockCollector.PlaceBlock.
/// Subclasses call base.PlaceBlock, so patching Block covers all of them.
/// </summary>
[HarmonyPatch(typeof(Block), nameof(Block.PlaceBlock))]
public static class Patch_PlaceBlock
{
    private static void Postfix(WorldBase _world, BlockPlacement.Result _result, EntityAlive _ea)
    {
        try
        {
            if (!ChaoticPickup.Enabled) return;
            if (_ea == null || _ea.entityType != EntityType.Player) return;

            TileEntity te = _world.GetTileEntity(_result.blockPos) as TileEntity;
            if (te == null) return;

            ITileEntityLootable loot;
            if (!te.TryGetSelfOrFeature(out loot)) return;
            if (loot.bPlayerStorage) return;

            loot.bPlayerStorage = true;
            loot.SetEmpty();
            loot.SetModified();
        }
        catch (Exception e)
        {
            Log.Error("[ChaoticPickup] PlaceBlock: " + e);
        }
    }
}

/// <summary>One line in the log once the whole block list has finished loading.</summary>
[HarmonyPatch(typeof(Block), nameof(Block.LateInitAll))]
public static class Patch_LogSummary
{
    private static void Postfix()
    {
        ChaoticPickup.LogSummary();
    }
}
