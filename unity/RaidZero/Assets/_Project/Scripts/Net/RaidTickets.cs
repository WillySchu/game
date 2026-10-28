using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>A place in a raid, as the backend's POST /v1/me/raid-tickets hands it out.</summary>
    public sealed class RaidTicket
    {
        public RaidTicket(string raidId, string joinToken)
        {
            RaidId = raidId;
            JoinToken = joinToken;
        }

        public string RaidId { get; }

        public string JoinToken { get; }
    }

    /// <summary>
    /// Where the client gets the ticket it presents when it connects to a game server.
    ///
    /// The Hideout will ask the backend for a ticket, set <see cref="Next"/>, and connect to the server address
    /// that came with it. Until the Hideout exists, pressing Play in the editor gets a ticket on its own:
    /// a dev login under a fresh name (so every play session starts with a new starter kit) against the
    /// backend in <see cref="DevSettings"/>.
    /// </summary>
    public static class RaidTickets
    {
        /// <summary>Set before connecting; used once.</summary>
        public static RaidTicket Next { get; set; }

        public static async Task<RaidTicket> TakeAsync()
        {
            var ticket = Next;
            Next = null;
            if (ticket != null)
            {
                return ticket;
            }

            if (!Application.isEditor)
            {
                throw new InvalidOperationException("No raid ticket. Join a raid from the Hideout.");
            }

            var name = "dev-" + UnityEngine.Random.Range(10000, 99999);
            Debug.Log("No raid ticket set, so getting a dev ticket as " + name + " from " + DevSettings.BackendUrl + ".");
            return await DevTicketAsync(DevSettings.BackendUrl, name, DevSettings.MapId);
        }

        /// <summary>Dev login, then a ticket. Only works against a backend with dev login enabled.</summary>
        public static async Task<RaidTicket> DevTicketAsync(string backendUrl, string displayName, string mapId)
        {
            using (var http = new HttpClient { BaseAddress = new Uri(backendUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) })
            {
                var login = await PostAsync(http, "v1/auth/dev-login", new JObject { ["display_name"] = displayName }, null);
                var ticket = await PostAsync(http, "v1/me/raid-tickets", new JObject { ["map_id"] = mapId }, (string)login["token"]);
                return new RaidTicket((string)ticket["raid_id"], (string)ticket["join_token"]);
            }
        }

        private static async Task<JObject> PostAsync(HttpClient http, string path, JObject body, string bearer)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, path))
            {
                request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
                if (bearer != null)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                }

                using (var response = await http.SendAsync(request))
                {
                    var text = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException("POST /" + path + " answered " + (int)response.StatusCode + ": " + text);
                    }

                    return JObject.Parse(text);
                }
            }
        }
    }
}
