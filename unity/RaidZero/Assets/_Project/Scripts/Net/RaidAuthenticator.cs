using System;
using System.Collections.Generic;
using FishNet.Authenticating;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>
    /// Lets a client into the game server only with a valid raid ticket from the backend. Assign it to the
    /// ServerManager's Authenticator field. It goes on the same GameObject as <see cref="RaidServer"/>.
    ///
    /// The client, as soon as it connects, sends its ticket (<see cref="RaidTickets"/>). The server redeems it
    /// with the backend, which moves the player's gear into the raid, and only then lets the connection in.
    /// The host in the editor is a player too, so it goes through exactly the same steps.
    /// </summary>
    [RequireComponent(typeof(RaidServer))]
    public sealed class RaidAuthenticator : Authenticator
    {
        private readonly HashSet<NetworkConnection> _joining = new HashSet<NetworkConnection>();

        public override event Action<NetworkConnection, bool> OnAuthenticationResult;

        /// <summary>
        /// On the client: the server's answer to our ticket. When it fails, Error is a JoinResult code; for
        /// ticket_expired or invalid_join_token the Hideout should get a new ticket.
        /// </summary>
        public static event Action<JoinRaidResultBroadcast> JoinAnswered;

        public override void InitializeOnce(NetworkManager networkManager)
        {
            base.InitializeOnce(networkManager);
            networkManager.ClientManager.OnClientConnectionState += OnClientConnectionState;
            networkManager.ClientManager.RegisterBroadcast<JoinRaidResultBroadcast>(OnJoinResult);

            // Not authenticated yet when this arrives, by definition.
            networkManager.ServerManager.RegisterBroadcast<JoinRaidBroadcast>(OnJoinRequest, requireAuthentication: false);
        }

        // ---- Client ----

        private async void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState != LocalConnectionState.Started)
            {
                return;
            }

            try
            {
                var ticket = await RaidTickets.TakeAsync();
                NetworkManager.ClientManager.Broadcast(new JoinRaidBroadcast
                {
                    RaidId = ticket.RaidId,
                    JoinToken = ticket.JoinToken,
                    Fingerprint = NetworkFingerprint.Current,
                });
            }
            catch (Exception exception)
            {
                Debug.LogError("[raid] Couldn't get a raid ticket: " + exception.Message);
                NetworkManager.ClientManager.StopConnection();
            }
        }

        private void OnJoinResult(JoinRaidResultBroadcast result, Channel channel)
        {
            if (!result.Success)
            {
                Debug.LogWarning("[raid] The server refused our ticket: " + result.Error + " " + result.Message);
            }

            JoinAnswered?.Invoke(result);
        }

        // ---- Server ----

        private void Refuse(NetworkConnection connection, string error, string message)
        {
            NetworkManager.ServerManager.Broadcast(connection, new JoinRaidResultBroadcast { Success = false, Error = error, Message = message }, requireAuthenticated: false);
            OnAuthenticationResult?.Invoke(connection, false);
        }

        private async void OnJoinRequest(NetworkConnection connection, JoinRaidBroadcast request, Channel channel)
        {
            // An authenticated connection asking again, or a second request while the first is in flight, is
            // a misbehaving client.
            if (connection.IsAuthenticated || !_joining.Add(connection))
            {
                connection.Disconnect(true);
                return;
            }

            try
            {
                // A client with different networked code would be kicked on its first message that differs, so
                // turn it away now, before the backend moves its gear. Its ticket stays good for after it updates.
                if (request.Fingerprint != NetworkFingerprint.Current)
                {
                    Debug.LogWarning("[raid] Refused a client with network code " + (request.Fingerprint ?? "(none)")
                        + "; this server has " + NetworkFingerprint.Current + ".");
                    Refuse(connection, "version_mismatch",
                        "Your game's network code (" + (request.Fingerprint ?? "unknown") + ") differs from the server's (" + NetworkFingerprint.Current
                        + "). Everyone needs the same version: pull the latest, and update the server.");
                    return;
                }

                var raidServer = GetComponent<RaidServer>();
                var (success, error, message) = await raidServer.JoinAsync(connection, request.RaidId, request.JoinToken);
                if (!connection.IsActive)
                {
                    return;
                }

                // The answer goes out before the result, which disconnects a refused client.
                NetworkManager.ServerManager.Broadcast(connection, new JoinRaidResultBroadcast { Success = success, Error = error, Message = message }, requireAuthenticated: false);
                OnAuthenticationResult?.Invoke(connection, success);
                if (success)
                {
                    raidServer.OnAuthenticated(connection);
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                if (connection.IsActive)
                {
                    OnAuthenticationResult?.Invoke(connection, false);
                }
            }
            finally
            {
                _joining.Remove(connection);
            }
        }
    }
}
