using FishNet.Broadcast;

namespace RaidZero.Net
{
    /// <summary>Client to server, right after connecting: the ticket the backend issued for this raid.</summary>
    public struct JoinRaidBroadcast : IBroadcast
    {
        public string RaidId;
        public string JoinToken;
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
}
