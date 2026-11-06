#if UNITY_EDITOR
using Unity.Multiplayer;
#endif

namespace RaidZero.World
{
    /// <summary>
    /// Whether this process is a raid client, a raid server, or both, which decides the scenes it loads
    /// (core.client, core.server) and so the components it gets.
    ///
    /// A build is what its build profile made it: BUILD_CLIENT and BUILD_SERVER. The editor compiles both, and
    /// each play mode instance takes the multiplayer role chosen in its toolbar: the role dropdown next to Play
    /// in the main editor, and the same dropdown in each Multiplayer Play Mode clone. Client and Server makes a
    /// host (a server with a player on it); a clone set to Client joins it. Set them before pressing Play.
    /// </summary>
    public static class ProcessRole
    {
        public static bool IsClient
        {
            get
            {
#if UNITY_EDITOR
                return (EditorRole & MultiplayerRoleFlags.Client) != 0;
#elif BUILD_CLIENT
                return true;
#else
                return false;
#endif
            }
        }

        public static bool IsServer
        {
            get
            {
#if UNITY_EDITOR
                return (EditorRole & MultiplayerRoleFlags.Server) != 0;
#elif BUILD_SERVER
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>"client", "server" or "client and server", for logs.</summary>
        public static string Describe() => IsClient && IsServer ? "client and server" : IsServer ? "server" : IsClient ? "client" : "neither client nor server";

#if UNITY_EDITOR
        // With multiplayer roles turned off (or none chosen), the editor plays both, as it did before roles.
        private static MultiplayerRoleFlags EditorRole
        {
            get
            {
                var role = MultiplayerRolesManager.ActiveMultiplayerRoleMask;
                return role == 0 ? MultiplayerRoleFlags.ClientAndServer : role;
            }
        }
#endif
    }
}
