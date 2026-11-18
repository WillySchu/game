using FishNet.Broadcast;
using RaidZero.Shared.Raid;
using UnityEngine;

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

    /// <summary>
    /// Client to server, for testing only: kill me. The server only listens for it in the editor and in
    /// Development builds (the F1 overlay's Die button), until there's damage to die from.
    /// </summary>
    public struct DevDieBroadcast : IBroadcast
    {
    }

    /// <summary>
    /// Client to server, for testing only: as if shot with a 5.45 there (RaidServer.Hit). The server only listens
    /// for it in the editor and in Development builds (the F1 overlay's Shot buttons), until there are hitboxes.
    /// </summary>
    public struct DevShotBroadcast : IBroadcast
    {
        public BodyPart Part;
    }

    /// <summary>
    /// Client to server, for testing only: spawn a target dummy in front of me (RaidServer.SpawnAI). Like the Shot
    /// buttons, the server only listens for it in the editor and Development builds.
    /// </summary>
    public struct DevSpawnDummyBroadcast : IBroadcast
    {
        /// <summary>With a brain: it guards where it stands, investigates, attacks (without shooting), searches and retreats. Without, it only turns to look.</summary>
        public bool Thinks;
    }

    /// <summary>
    /// Client to server, for testing only: send me (or stop sending me) what every AI is thinking, for the F1
    /// overlay's AI debug view. Like the Shot buttons, the server only listens for it in the editor and Development
    /// builds, so a release server never tells anyone where its AI are.
    /// </summary>
    public struct DevWatchAIBroadcast : IBroadcast
    {
        public bool Watch;
    }

    /// <summary>One AI, as the AI debug view shows it (see <see cref="DevAIDebugBroadcast"/>).</summary>
    public struct AIDebugEntry
    {
        public string Id;

        /// <summary>Above its head, where the label goes.</summary>
        public Vector3 LabelAt;

        /// <summary>What it's doing: "calm", "heard", "squad", "noticed", "alert" (the brain's states, once it has one).</summary>
        public string State;

        /// <summary>A line or two of detail: who it's aware of and how much, its faction.</summary>
        public string Detail;

        /// <summary>Whether it's looking at something: what it's noticed, or a noise, or its squad's sighting.</summary>
        public bool HasLookPoint;
        public Vector3 LookPoint;

        /// <summary>How aware it is of whoever it's most aware of, 0 to 1.</summary>
        public float Awareness;

        /// <summary>What its brain is doing ("Attack: hold (in cover), firing"); null without a brain.</summary>
        public string Brain;

        /// <summary>Where it's going (straight there, for the stand-in body; its path's next corner, with a real one).</summary>
        public bool HasMoveTarget;
        public Vector3 MoveTarget;

        /// <summary>The cover it's taken or heading for.</summary>
        public bool HasCover;
        public Vector3 Cover;
    }

    /// <summary>
    /// Server to client, for testing only: every AI in the raid, a few times a second, to a client that asked
    /// (<see cref="DevWatchAIBroadcast"/>). Only sent by servers in the editor and Development builds.
    /// </summary>
    public struct DevAIDebugBroadcast : IBroadcast
    {
        public AIDebugEntry[] Entries;
    }

    /// <summary>
    /// Server to client: your health, when you join or rejoin and whenever it changes. Only ever sent to the player
    /// it's about; nobody else learns anyone's health.
    /// </summary>
    public struct HealthBroadcast : IBroadcast
    {
        public float Health;
        public float Max;
    }

    /// <summary>Server to client: your extraction countdown started (or started over), or stopped (Point is null).</summary>
    public struct ExtractionBroadcast : IBroadcast
    {
        public string Point;
        public float SecondsLeft;
    }
}
