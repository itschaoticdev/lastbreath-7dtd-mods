using System;
using HarmonyLib;

namespace ChaoticBeastmaster
{
    /// <summary>
    /// Makes /pet reach the mod on every kind of game, not just a dedicated server.
    ///
    /// The mod used to read chat through ModEvents.ChatMessage alone. That works on a dedicated
    /// server and nowhere else, for two separate reasons that both trace back to the same wrong
    /// assumption - that a chat line always arrives from a network client:
    ///
    ///   1. A remote player's line arrives as NetPackageChat, whose ProcessPackage calls
    ///      GameManager.ChatMessageServer(base.Sender, ...) - a real ClientInfo. But the HOST of a
    ///      client-hosted game types into XUiC_Chat, which calls ChatMessageServer(**null**, ...)
    ///      directly, because the host is not a client of their own server. The old handler opened
    ///      with `if (ci == null) return false`, so for the host every /pet command fell straight
    ///      through and got broadcast as ordinary chat text. That is exactly the "text commands do
    ///      not register" report.
    ///   2. ModEvents dispatch stops at the first handler that returns StopHandlersAndVanilla, so
    ///      any other chat mod loading before us could swallow the line before we ever see it.
    ///
    /// So the hook moves to a Harmony prefix on ChatMessageServer itself, which runs *before*
    /// ModEvents.ChatMessage.Invoke and cannot be pre-empted by another mod's handler. Same
    /// reasoning as ApplyAnimalTags: stop depending on a channel that can be taken away, and do it
    /// from code. The ModEvents handler stays registered as a fallback for the case where this
    /// patch fails to apply against a future build - PetCommands.Handle is idempotent per line, so
    /// whichever path fires first wins and the other sees nothing.
    /// </summary>
    public static class Patch_GameManager_ChatMessageServer
    {
        /// <summary>
        /// Applied by hand rather than by PatchAll, and on its own, because Harmony binds prefix
        /// arguments by parameter NAME: the day TFP rename _msg, an attribute-driven patch would
        /// throw inside PatchAll and take the taming patches down with it. Here the worst case is
        /// that chat falls back to ModEvents and everything else keeps working.
        /// </summary>
        public static void Apply(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(GameManager), "ChatMessageServer");
            if (target == null)
            {
                Log.Warning("[Beastmaster] GameManager.ChatMessageServer not found; /pet falls back "
                    + "to ModEvents and will not work for the host of a client-hosted game.");
                return;
            }

            harmony.Patch(target, new HarmonyMethod(AccessTools.Method(
                typeof(Patch_GameManager_ChatMessageServer), nameof(Prefix))));
        }

        /// <summary>Returning false skips the original, which is what swallows the command line.</summary>
        public static bool Prefix(ClientInfo _cInfo, int _senderEntityId, string _msg)
        {
            try
            {
                return !PetCommands.Handle(_cInfo, _senderEntityId, _msg);
            }
            catch (Exception e)
            {
                // Never let a bug in here eat somebody's chat.
                Log.Error("[Beastmaster] chat hook failed, passing the message through: " + e);
                return true;
            }
        }
    }
}
