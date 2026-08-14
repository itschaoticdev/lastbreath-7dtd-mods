using HarmonyLib;

namespace PersistentParties
{
    /// <summary>
    /// The whole trick of this mod is which of these we hook.
    ///
    ///   ServerHandleAcceptInvite  -> someone joined      -> REMEMBER
    ///   ServerHandleLeaveParty    -> someone chose to go -> FORGET
    ///   ServerHandleKickParty     -> someone was removed -> FORGET
    ///   ServerHandleDisconnectParty -> someone logged off -> deliberately NOT hooked,
    ///                                  which is what makes the party survive a relog.
    ///
    /// EntityPlayer.LeaveParty() is also left alone on purpose: the game calls it
    /// directly when a party auto-dissolves down to one member, and it does not route
    /// through ServerHandleLeaveParty, so hooking it would erase saved parties every
    /// time the second-to-last member disconnected.
    /// </summary>
    [HarmonyPatch(typeof(Party), nameof(Party.ServerHandleAcceptInvite))]
    internal static class Patch_AcceptInvite
    {
        private static void Postfix(EntityPlayer invitedEntity)
        {
            if (!ModApi.IsServer || invitedEntity == null || invitedEntity.Party == null) return;
            Store.Remember(ModApi.UidsOf(invitedEntity.Party));
        }
    }

    [HarmonyPatch(typeof(Party), nameof(Party.ServerHandleLeaveParty))]
    internal static class Patch_LeaveParty
    {
        // Prefix: read the membership before the game tears it down.
        private static void Prefix(EntityPlayer player, out string __state)
        {
            __state = player != null ? ModApi.UidOf(player.entityId) : null;
        }

        private static void Postfix(string __state)
        {
            if (!ModApi.IsServer || __state == null) return;
            Store.Forget(__state);
        }
    }

    [HarmonyPatch(typeof(Party), nameof(Party.ServerHandleKickParty))]
    internal static class Patch_KickParty
    {
        private static void Prefix(int entityID, out string __state)
        {
            __state = ModApi.UidOf(entityID);
        }

        private static void Postfix(string __state)
        {
            if (!ModApi.IsServer || __state == null) return;
            Store.Forget(__state);
        }
    }
}
