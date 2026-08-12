using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ChaoticBeastmaster
{
    /// <summary>
    /// The player-facing side of the kennel: /pet in chat.
    ///
    /// Chat rather than a console command because console commands are admin-gated, and chat is
    /// the only input channel that works with no client-side install - which is the whole point of
    /// this mod being server-side.
    ///
    /// Nothing in here may depend on a ClientInfo. The host of a client-hosted game has none (see
    /// ChatHook), and requiring one is what stopped /pet working for every non-dedicated game.
    /// </summary>
    public static class PetCommands
    {
        private static readonly List<int> One = new List<int>();

        /// <summary>The first word that marks a line as ours. Whole-word, so /petition is chat.</summary>
        private static readonly string[] Prefixes = { "/pet", "/pets", "/beast", "/beastmaster", "/bm" };

        /// <summary>Set while we are sending a reply, so our own output cannot re-enter the hook.</summary>
        private static bool replying;

        /// <summary>Set by `bm pet` so the answer also lands in the console the command came from.</summary>
        public static Action<string> Echo;

        /// <summary>Last line acted on, so the Harmony hook and the ModEvents fallback cannot both run it.</summary>
        private static int lastFrame = -1;
        private static int lastSender = -1;
        private static string lastMessage;

        /// <summary>Returns true if the message was a /pet command and should not reach chat.</summary>
        public static bool Handle(ClientInfo ci, int senderEntityId, string message)
        {
            if (replying || string.IsNullOrEmpty(message)) return false;
            if (!TameRuntime.IsServer) return false;

            string msg = message.Trim();
            if (!IsPetCommand(msg)) return false;

            // Both the Harmony prefix and the ModEvents handler feed this. Only one of them can
            // fire per line in practice, but if a future build changes that, running the command
            // twice would stow a pet and then report "nothing is out".
            if (senderEntityId == lastSender && Time.frameCount == lastFrame && msg == lastMessage) return true;
            lastFrame = Time.frameCount;
            lastSender = senderEntityId;
            lastMessage = msg;

            EntityPlayer player = ResolvePlayer(ci, senderEntityId);
            if (player == null)
            {
                // Swallowed either way: a /pet line broadcast to everyone as chat is worse than
                // silence, and it is what the bug looked like from the outside.
                Log.Warning("[Beastmaster] '" + msg + "' came from entity " + senderEntityId
                    + " but no player entity could be found for it.");
                return true;
            }

            // Logged unconditionally: skipping the original ChatMessageServer also skips vanilla's
            // "Chat handled by mod" line, and a command that leaves no trace at all is what made
            // the last report impossible to tell apart from the mod not being installed.
            Log.Out("[Beastmaster] " + player.EntityName + " (entity " + senderEntityId + "): " + msg);

            string[] parts = msg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            RunFor(player, parts, 1);
            return true;
        }

        /// <summary>
        /// Runs a command for a player. Shared by chat and by `bm pet ...`, which exists so a host
        /// or admin always has a way in even if some other mod owns the chat line.
        /// </summary>
        /// <param name="parts">The whole line, split on spaces.</param>
        /// <param name="verbAt">Index of the verb within it.</param>
        public static void RunFor(EntityPlayer player, string[] parts, int verbAt)
        {
            int id = player.entityId;

            string ownerKey = TameRuntime.OwnerKeyOf(player);
            if (string.IsNullOrEmpty(ownerKey))
            {
                Reply(id, "Could not identify your account - companions are unavailable.");
                Log.Warning("[Beastmaster] no owner key for " + player.EntityName + " (entity " + id
                    + "); /pet cannot work and taming will not persist.");
                return;
            }

            string verb = parts.Length > verbAt ? parts[verbAt].ToLowerInvariant() : "list";
            TameRuntime.DebugLog("/pet '" + verb + "' from " + player.EntityName + " (" + ownerKey + ")");

            switch (verb)
            {
                case "list":
                case "":        DoList(id, ownerKey); break;
                case "stow":
                case "away":    DoStow(id, ownerKey); break;
                case "call":
                case "out":     DoCall(id, ownerKey, player, Arg(parts, verbAt + 1)); break;
                case "release":
                case "free":    DoRelease(id, ownerKey, Arg(parts, verbAt + 1)); break;
                default:        DoHelp(id); break;
            }
        }

        /// <summary>Whole-word match on the first token, so "/petunia" is still just chat.</summary>
        private static bool IsPetCommand(string msg)
        {
            if (msg.Length == 0 || msg[0] != '/') return false;

            int end = msg.IndexOf(' ');
            string head = (end < 0 ? msg : msg.Substring(0, end)).ToLowerInvariant();
            for (int i = 0; i < Prefixes.Length; i++)
            {
                if (head == Prefixes[i]) return true;
            }
            return false;
        }

        /// <summary>
        /// The sender entity id is the truth on every kind of game - XUiC_Chat passes the local
        /// player's, NetPackageChat passes the remote one's. The ClientInfo is only a fallback for
        /// the case where a client sent a line while its entity was mid-swap (death, teleport).
        /// </summary>
        private static EntityPlayer ResolvePlayer(ClientInfo ci, int senderEntityId)
        {
            var world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return null;

            EntityPlayer player = world.GetEntity(senderEntityId) as EntityPlayer;
            if (player == null && ci != null && ci.entityId >= 0)
                player = world.GetEntity(ci.entityId) as EntityPlayer;

            return player;
        }

        private static string Arg(string[] parts, int i)
        {
            return parts.Length > i ? parts[i] : null;
        }

        private static void DoHelp(int id)
        {
            Reply(id,
                "[BEASTMASTER]\n" +
                "/pet list          - what you own\n" +
                "/pet call <n>      - bring one out (puts the current one away)\n" +
                "/pet stow          - put your companion away\n" +
                "/pet release <n>   - set one free for good");
        }

        private static void DoList(int id, string ownerKey)
        {
            var all = PetStore.All(ownerKey);
            if (all.Count == 0)
            {
                Reply(id, "You have no companions. Feed a wolf or bear "
                    + BmConfig.FeedsToTame + " times to tame one.");
                return;
            }

            var sb = new StringBuilder();
            sb.Append("[BEASTMASTER] ").Append(all.Count).Append('/').Append(BmConfig.MaxOwnedPets).Append(" owned:\n");
            for (int i = 0; i < all.Count; i++)
            {
                sb.Append("  ").Append(i + 1).Append(". ")
                  .Append(TameRuntime.PrettyClassName(all[i].EntityClassName))
                  .Append(all[i].Active ? "   <- out now" : "   (kennelled)")
                  .Append('\n');
            }
            sb.Append("/pet call <n> to swap.");
            Reply(id, sb.ToString());
        }

        private static void DoStow(int id, string ownerKey)
        {
            var active = PetStore.GetActive(ownerKey);
            if (active == null)
            {
                Reply(id, "Nothing is out.");
                return;
            }
            TameRuntime.DespawnLivePetOf(ownerKey);
            PetStore.StowAll(ownerKey);
            Reply(id, TameRuntime.PrettyClassName(active.EntityClassName) + " slinks off to wait for you.");
        }

        private static void DoCall(int id, string ownerKey, EntityPlayer player, string arg)
        {
            var all = PetStore.All(ownerKey);
            if (all.Count == 0) { Reply(id, "You have no companions."); return; }

            int idx;
            if (arg == null)
            {
                // No number given: if exactly one is owned, that is obviously the one meant.
                if (all.Count == 1) idx = 0;
                else { Reply(id, "Which one? /pet list"); return; }
            }
            else if (!int.TryParse(arg, out idx) || idx < 1 || idx > all.Count)
            {
                Reply(id, "No companion numbered '" + arg + "'. /pet list");
                return;
            }
            else idx--;

            if (all[idx].Active && TameRuntime.HasLivePet(ownerKey))
            {
                Reply(id, TameRuntime.PrettyClassName(all[idx].EntityClassName) + " is already out.");
                return;
            }

            // Only ever one out at a time.
            TameRuntime.DespawnLivePetOf(ownerKey);
            PetStore.SetActive(ownerKey, idx);
            PetRespawner.QueueSpawn(player, ownerKey, all[idx].EntityClassName, 0.5f);

            Reply(id, TameRuntime.PrettyClassName(all[idx].EntityClassName) + " comes when you call.");
        }

        private static void DoRelease(int id, string ownerKey, string arg)
        {
            var all = PetStore.All(ownerKey);
            if (all.Count == 0) { Reply(id, "You have no companions."); return; }

            int idx;
            if (arg == null || !int.TryParse(arg, out idx) || idx < 1 || idx > all.Count)
            {
                Reply(id, "Which one? /pet release <n>  (see /pet list)");
                return;
            }
            idx--;

            if (all[idx].Active) TameRuntime.DespawnLivePetOf(ownerKey);
            PetRecord gone = PetStore.RemoveAt(ownerKey, idx);
            if (gone == null) { Reply(id, "Could not release that one."); return; }

            Reply(id, TameRuntime.PrettyClassName(gone.EntityClassName)
                + " goes back to the wild. It will not come back.");
        }

        /// <summary>
        /// Private message from the server to one player. ChatMessageServer delivers to a local
        /// host too - it hands the line to ChatMessageClient before it does anything with the
        /// network - so this one call covers both kinds of game.
        /// </summary>
        public static void Reply(int entityId, string text)
        {
            try
            {
                var echo = Echo;
                if (echo != null) echo(text);

                replying = true;
                One.Clear();
                One.Add(entityId);
                GameManager.Instance.ChatMessageServer(null, EChatType.Whisper, entityId,
                    text, One, EMessageSender.Server, GeneratedTextManager.BbCodeSupportMode.Supported);
            }
            catch (Exception e)
            {
                TameRuntime.DebugLog("chat reply failed: " + e.Message);
            }
            finally
            {
                replying = false;
            }
        }
    }
}
