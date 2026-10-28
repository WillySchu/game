using FishNet.Broadcast;
using RaidZero.Shared.Raid;

namespace RaidZero.Net
{
    /// <summary>
    /// Client to server, right after connecting: the ticket the backend issued for this raid, and the client's
    /// NetworkFingerprint, which must match the server's.
    /// </summary>
    public struct JoinRaidBroadcast : IBroadcast
    {
        public string RaidId;
        public string JoinToken;
        public string Fingerprint;
    }

    /// <summary>Server to client: whether the join worked, and if not, why (a JoinResult error code).</summary>
    public struct JoinRaidResultBroadcast : IBroadcast
    {
        public bool Success;
        public string Error;
        public string Message;
    }

    /// <summary>Client to server: one raid inventory message, encoded by RaidProtocol.</summary>
    public struct InventoryToServerBroadcast : IBroadcast
    {
        public byte[] Data;
    }

    /// <summary>Server to client: one raid inventory message, encoded by RaidProtocol.</summary>
    public struct InventoryToClientBroadcast : IBroadcast
    {
        public byte[] Data;
    }

    /// <summary>
    /// Server to client: where the raid is and how long it has left. Sent when the player joins and whenever
    /// the phase changes; clients count down from it themselves.
    /// </summary>
    public struct RaidStateBroadcast : IBroadcast
    {
        public RaidPhase Phase;
        public float SecondsRemaining;
        public float Duration;
    }

    /// <summary>Server to client: you're out of the raid, and how. The server disconnects them a moment later.</summary>
    public struct LeftRaidBroadcast : IBroadcast
    {
        public RaidExit Exit;
    }

    /// <summary>Server to client: your extraction countdown started (or started over), or stopped (Point is null).</summary>
    public struct ExtractionBroadcast : IBroadcast
    {
        public string Point;
        public float SecondsLeft;
    }
}
