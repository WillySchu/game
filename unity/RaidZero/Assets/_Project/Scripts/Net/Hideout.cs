using System.Threading.Tasks;
using FishNet.Managing;
using RaidZero.BackendApi;
using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>
    /// The Hideout's way into the backend and into raids. The Hideout screens use <see cref="Session"/> for the
    /// player's profile, stash edits and raid results, and <see cref="DeployAsync"/> to go into a raid.
    /// </summary>
    public static class Hideout
    {
        private static PlayerSession _session;

        /// <summary>
        /// The player's session, created on first use. Log in through it (DevLoginAsync for now), and listen to
        /// its ProfileChanged and LoggedOut events.
        /// </summary>
        public static PlayerSession Session => _session ?? (_session = new PlayerSession(BackendUrl, ServerConfig.LoadCatalog()));

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

        /// <summary>
        /// Goes into a raid: asks the backend for a ticket on this map, then connects to the server the ticket
        /// names, which redeems it (RaidAuthenticator). Load the raid scene however the game does it once the
        /// connection is up. If the server turns the ticket down, RaidAuthenticator.JoinAnswered says why.
        /// </summary>
        /// <returns>The ticket, or why there isn't one: already_in_raid, no_server_available, logged_out and so on.</returns>
        public static async Task<PlayerResult<RaidTicketInfo>> DeployAsync(NetworkManager networkManager, string mapId)
        {
            var ticket = await Session.RequestTicketAsync(mapId);
            if (!ticket.Succeeded)
            {
                return ticket;
            }

            RaidTickets.Next = new RaidTicket(ticket.Value.RaidId, ticket.Value.JoinToken);
            if (!networkManager.ClientManager.StartConnection(ticket.Value.ServerHost, (ushort)ticket.Value.ServerPort))
            {
                Debug.LogError("[raid] Couldn't start connecting to " + ticket.Value.ServerHost + ":" + ticket.Value.ServerPort + ".");
            }

            return ticket;
        }
    }
}
