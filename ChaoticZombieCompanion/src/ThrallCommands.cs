using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ChaoticZombieCompanion
{
    /// <summary>
    /// The player-facing side of the roster: /thrall in chat.
    ///
    /// Chat rather than a console command because console commands are admin-gated, and chat is the
    /// only input channel that works with no client-side install - which is the whole point of this
    /// mod being server-side.
    ///
    /// Nothing in here may depend on a ClientInfo. The host of a client-hosted game has none (see
    /// ChatHook), and requiring one is what stopped the same commands working for every
    /// non-dedicated game in Beastmaster.
    /// </summary>
    public static class ThrallCommands
    {
        private static readonly List<int> One = new List<int>();

        /// <summary>The first word that marks a line as ours. Whole-word, so /zone is chat.</summary>
        private static readonly string[] Prefixes = { "/thrall", "/thralls", "/zombie", "/zombies", "/zc", "/z" };

        /// <summary>Set while we are sending a reply, so our own output cannot re-enter the hook.</summary>
        private static bool replying;

        /// <summary>Set by `zc thrall` so the answer also lands in the console it came from.</summary>
        public static Action<string> Echo;

        /// <summary>Last line acted on, so the Harmony hook and the ModEvents fallback cannot both run it.</summary>
        private static int lastFrame = -1;
        private static int lastSender = -1;
        private static string lastMessage;

        /// <summary>Returns true if the message was one of ours and should not reach chat.</summary>
        public static bool Handle(ClientInfo ci, int senderEntityId, string message)
        {
            if (replying || string.IsNullOrEmpty(message)) return false;
            if (!ThrallRuntime.IsServer) return false;

            string msg = message.Trim();
            if (!IsThrallCommand(msg)) return false;

            // Both the Harmony prefix and the ModEvents handler feed this. Only one of them can
            // fire per line in practice, but if a future build changes that, running the command
            // twice would stow a thrall and then report "nothing is out".
            if (senderEntityId == lastSender && Time.frameCount == lastFrame && msg == lastMessage) return true;
            lastFrame = Time.frameCount;
            lastSender = senderEntityId;
            lastMessage = msg;

            EntityPlayer player = ResolvePlayer(ci, senderEntityId);
            if (player == null)
            {
                // Swallowed either way: a /thrall line broadcast to everyone as chat is worse than
                // silence, and it is exactly what the bug looks like from the outside.
                Log.Warning("[ZombieCompanion] '" + msg + "' came from entity " + senderEntityId
                    + " but no player entity could be found for it.");
                return true;
            }

            // Logged unconditionally: skipping the original ChatMessageServer also skips vanilla's
            // "Chat handled by mod" line, and a command that leaves no trace at all cannot be told
            // apart from the mod not being installed.
            Log.Out("[ZombieCompanion] " + player.EntityName + " (entity " + senderEntityId + "): " + msg);

            string[] parts = msg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            RunFor(player, parts, 1);
            return true;
        }

        /// <summary>
        /// Runs a command for a player. Shared by chat and by `zc thrall ...`, which exists so a
        /// host or admin always has a way in even if another mod owns the chat line.
        /// </summary>
        /// <param name="parts">The whole line, split on spaces.</param>
        /// <param name="verbAt">Index of the verb within it.</param>
        public static void RunFor(EntityPlayer player, string[] parts, int verbAt)
        {
            int id = player.entityId;

            string ownerKey = ThrallRuntime.OwnerKeyOf(player);
            if (string.IsNullOrEmpty(ownerKey))
            {
                Reply(id, "Could not identify your account - thralls are unavailable.");
                Log.Warning("[ZombieCompanion] no owner key for " + player.EntityName + " (entity " + id
                    + "); /thrall cannot work and binding will not persist.");
                return;
            }

            string verb = parts.Length > verbAt ? parts[verbAt].ToLowerInvariant() : "list";
            ThrallRuntime.DebugLog("/thrall '" + verb + "' from " + player.EntityName + " (" + ownerKey + ")");

            switch (verb)
            {
                case "list":
                case "":        DoList(id, ownerKey); break;
                case "stow":
                case "away":    DoStow(id, ownerKey, Arg(parts, verbAt + 1)); break;
                case "call":
                case "out":     DoCall(id, ownerKey, player, Arg(parts, verbAt + 1)); break;
                case "release":
                case "free":    DoRelease(id, ownerKey, Arg(parts, verbAt + 1)); break;
                default:        DoHelp(id); break;
            }
        }

        /// <summary>Whole-word match on the first token, so "/zoneinfo" is still just chat.</summary>
        private static bool IsThrallCommand(string msg)
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
                "[ZOMBIE COMPANION]\n" +
                "/thrall list        - what you are holding\n" +
                "/thrall call <n>    - bring one out (up to " + ZcConfig.MaxActiveThralls + " at once)\n" +
                "/thrall stow [n]    - put one away, or all of them\n" +
                "/thrall release <n> - let one go for good");
        }

        private static void DoList(int id, string ownerKey)
        {
            var all = ThrallStore.All(ownerKey);
            if (all.Count == 0)
            {
                Reply(id, "You hold no thralls. Throw gore bait at a zombie and let it eat "
                    + ZcConfig.FeedsToThrall + " times (more for the harder tiers).");
                return;
            }

            var sb = new StringBuilder();
            sb.Append("[ZOMBIE COMPANION] ").Append(all.Count).Append('/').Append(ZcConfig.MaxOwnedThralls)
              .Append(" held, ").Append(ThrallStore.ActiveCount(ownerKey)).Append('/')
              .Append(ZcConfig.MaxActiveThralls).Append(" out:\n");
            for (int i = 0; i < all.Count; i++)
            {
                sb.Append("  ").Append(i + 1).Append(". ")
                  .Append(ThrallRuntime.PrettyClassName(all[i].EntityClassName))
                  .Append(all[i].Active ? "   <- out now" : "   (waiting)")
                  .Append('\n');
            }
            sb.Append("/thrall call <n> or /thrall stow <n>.");
            Reply(id, sb.ToString());
        }

        private static void DoStow(int id, string ownerKey, string arg)
        {
            var all = ThrallStore.All(ownerKey);
            if (ThrallStore.ActiveCount(ownerKey) == 0)
            {
                Reply(id, "Nothing is out.");
                return;
            }

            // No number: put the lot away. That is what someone typing /thrall stow in a hurry
            // means, and with several out there is no sensible single default.
            if (arg == null)
            {
                ThrallRuntime.DespawnAllOf(ownerKey);
                ThrallStore.StowAll(ownerKey);
                Reply(id, "They shuffle back into the dark.");
                return;
            }

            int idx;
            if (!int.TryParse(arg, out idx) || idx < 1 || idx > all.Count)
            {
                Reply(id, "No thrall numbered '" + arg + "'. /thrall list");
                return;
            }
            idx--;

            if (!all[idx].Active)
            {
                Reply(id, ThrallRuntime.PrettyClassName(all[idx].EntityClassName) + " is not out.");
                return;
            }

            int live = ThrallRuntime.FindLiveThrall(ownerKey, all[idx].EntityClassName);
            if (live >= 0) ThrallRuntime.DespawnThrall(live);
            ThrallStore.SetActive(ownerKey, idx, false);

            Reply(id, ThrallRuntime.PrettyClassName(all[idx].EntityClassName) + " shuffles back into the dark.");
        }

        private static void DoCall(int id, string ownerKey, EntityPlayer player, string arg)
        {
            var all = ThrallStore.All(ownerKey);
            if (all.Count == 0) { Reply(id, "You hold no thralls."); return; }

            int idx;
            if (arg == null)
            {
                // No number given: if exactly one is held, that is obviously the one meant.
                if (all.Count == 1) idx = 0;
                else { Reply(id, "Which one? /thrall list"); return; }
            }
            else if (!int.TryParse(arg, out idx) || idx < 1 || idx > all.Count)
            {
                Reply(id, "No thrall numbered '" + arg + "'. /thrall list");
                return;
            }
            else idx--;

            if (all[idx].Active)
            {
                Reply(id, ThrallRuntime.PrettyClassName(all[idx].EntityClassName) + " is already out.");
                return;
            }

            if (!ThrallStore.SetActive(ownerKey, idx, true))
            {
                Reply(id, "You already have " + ZcConfig.MaxActiveThralls
                    + " out. Stow one first: /thrall stow <n>");
                return;
            }

            ThrallRespawner.QueueSpawn(player, ownerKey, all[idx].EntityClassName, 0.5f);
            Reply(id, ThrallRuntime.PrettyClassName(all[idx].EntityClassName) + " hears you and comes.");
        }

        private static void DoRelease(int id, string ownerKey, string arg)
        {
            var all = ThrallStore.All(ownerKey);
            if (all.Count == 0) { Reply(id, "You hold no thralls."); return; }

            int idx;
            if (arg == null || !int.TryParse(arg, out idx) || idx < 1 || idx > all.Count)
            {
                Reply(id, "Which one? /thrall release <n>  (see /thrall list)");
                return;
            }
            idx--;

            if (all[idx].Active)
            {
                int live = ThrallRuntime.FindLiveThrall(ownerKey, all[idx].EntityClassName);
                if (live >= 0) ThrallRuntime.DespawnThrall(live);
            }

            ThrallRecord gone = ThrallStore.RemoveAt(ownerKey, idx);
            if (gone == null) { Reply(id, "Could not release that one."); return; }

            Reply(id, ThrallRuntime.PrettyClassName(gone.EntityClassName)
                + " wanders off after something else. It will not come back.");
        }

        /// <summary>
        /// Private message from the server to one player. ChatMessageServer delivers to a local host
        /// too - it hands the line to ChatMessageClient before it does anything with the network -
        /// so this one call covers both kinds of game.
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
                ThrallRuntime.DebugLog("chat reply failed: " + e.Message);
            }
            finally
            {
                replying = false;
            }
        }
    }
}
