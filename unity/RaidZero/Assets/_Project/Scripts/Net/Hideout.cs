using System.Threading.Tasks;
using FishNet.Managing;
using RaidZero.BackendApi;
using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>How a deploy went: the ticket used, or why there isn't one.</summary>
    public sealed class DeployResult
    {
        private DeployResult(RaidTicketInfo ticket, bool rejoined, string error, string message, string raidId)
        {
            Ticket = ticket;
            Rejoined = rejoined;
            Error = error;
            Message = message;
            RaidId = raidId;
        }

        public bool Succeeded => Error == null;

        public RaidTicketInfo Ticket { get; }

        /// <summary>The player was already in this raid (they'd disconnected), so this goes back into it.</summary>
        public bool Rejoined { get; }

        /// <summary>already_in_raid, no_server_available, logged_out and so on.</summary>
        public string Error { get; }

        public string Message { get; }

        /// <summary>For already_in_raid: the raid the player is still in.</summary>
        public string RaidId { get; }

        public override string ToString() => Succeeded ? (Rejoined ? "rejoined" : "ok") : Error + ": " + Message;

        internal static DeployResult Ok(RaidTicketInfo ticket, bool rejoined) => new DeployResult(ticket, rejoined, null, null, null);

        internal static DeployResult Failed(string error, string message, string raidId) => new DeployResult(null, false, error, message, raidId);
    }

    /// <summary>
    /// The Hideout's way into the backend and into raids. The Hideout screens use <see cref="Session"/> for the
    /// player's profile, stash edits and raid results, and <see cref="DeployAsync"/> to go into a raid.
    /// </summary>
    public static class Hideout
    {
        private static PlayerSession _session;
        private static string _sessionUrl;

        /// <summary>
        /// The player's session, created on first use. Log in through it (DevLoginAsync for now), and listen to
        /// its ProfileChanged and LoggedOut events. If the backend address changes (editing RaidZeroDev.json
        /// while the editor keeps this class's state between Play sessions), it starts a new, logged-out session
        /// for the new address.
        /// </summary>
        public static PlayerSession Session
        {
            get
            {
                var url = BackendUrl;
                if (_session == null || _sessionUrl != url)
                {
                    _session?.Dispose();
                    _session = new PlayerSession(url, ServerConfig.LoadCatalog());
                    _sessionUrl = url;
                    LastTicket = null;
                }

                return _session;
            }
        }

        /// <summary>
        /// The last ticket this client deployed with. Presenting it again gets a disconnected player back into
        /// their raid, since the server and backend treat a repeated join as a reconnect.
        /// </summary>
        public static RaidTicketInfo LastTicket { get; private set; }

        /// <summary>
        /// Where players' clients find the backend: --backend-url or RAIDZERO_BACKEND_URL, else, in the editor,
        /// UserSettings/RaidZeroDev.json. Release builds will carry the real address once there is one.
        /// </summary>
        public static string BackendUrl
        {
            get
            {
                var args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "--backend-url")
                    {
                        return args[i + 1];
                    }
                }

                var fromEnvironment = System.Environment.GetEnvironmentVariable("RAIDZERO_BACKEND_URL");
                if (!string.IsNullOrEmpty(fromEnvironment))
                {
                    return fromEnvironment;
                }

                return Application.isEditor ? DevSettings.BackendUrl : "http://localhost:5080";
            }
        }

        /// <summary>Forgets the last ticket, as when a different player logs in on this client.</summary>
        public static void ForgetLastTicket() => LastTicket = null;

        /// <summary>
        /// Goes into a raid: asks the backend for a ticket on this map, then connects to the server the ticket
        /// names, which redeems it (RaidAuthenticator). If the player is still in the raid this client last
        /// deployed to (they disconnected), it rejoins that raid with the same ticket instead. Load the raid
        /// scene however the game does it once the connection is up. If the server turns the ticket down,
        /// RaidAuthenticator.JoinAnswered says why.
        /// </summary>
        public static async Task<DeployResult> DeployAsync(NetworkManager networkManager, string mapId)
        {
            var requested = await Session.RequestTicketAsync(mapId);
            RaidTicketInfo ticket;
            bool rejoining;
            if (requested.Succeeded)
            {
                ticket = requested.Value;
                rejoining = false;
            }
            else if (requested.Error == "already_in_raid" && LastTicket != null && LastTicket.RaidId == requested.RaidId)
            {
                ticket = LastTicket;
                rejoining = true;
            }
            else
            {
                return DeployResult.Failed(requested.Error, requested.Message, requested.RaidId);
            }

            LastTicket = ticket;
            RaidTickets.Next = new RaidTicket(ticket.RaidId, ticket.JoinToken);
            if (!networkManager.ClientManager.StartConnection(ticket.ServerHost, (ushort)ticket.ServerPort))
            {
                Debug.LogError("[raid] Couldn't start connecting to " + ticket.ServerHost + ":" + ticket.ServerPort + ".");
            }

            return DeployResult.Ok(ticket, rejoining);
        }
    }
}
