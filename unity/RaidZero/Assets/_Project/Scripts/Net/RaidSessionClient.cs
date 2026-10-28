using System;
using FishNet.Managing;
using FishNet.Transporting;
using RaidZero.Shared.Raid;
using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>
    /// The client's view of the raid itself: its phase, the clock, the player's extraction countdown, and how
    /// they left. Goes on the NetworkManager's GameObject. The HUD reads it; the end-of-raid screen listens to
    /// <see cref="LeftRaid"/>, then sends the player back to the Hideout, which waits for the raid's result.
    /// </summary>
    [RequireComponent(typeof(NetworkManager))]
    public sealed class RaidSessionClient : MonoBehaviour
    {
        private NetworkManager _networkManager;
        private float _remainingAtReceipt;
        private float _stateReceivedAt;
        private float _extractionLeftAtReceipt;
        private float _extractionReceivedAt;

        public static RaidSessionClient Instance { get; private set; }

        public RaidPhase Phase { get; private set; } = RaidPhase.Waiting;

        /// <summary>The raid's full length, in seconds.</summary>
        public float Duration { get; private set; }

        /// <summary>Seconds on the clock: the full length while waiting, counting down in progress, zero after.</summary>
        public float SecondsRemaining
        {
            get
            {
                switch (Phase)
                {
                    case RaidPhase.Waiting:
                        return Duration;
                    case RaidPhase.InProgress:
                        return Mathf.Max(0, _remainingAtReceipt - (Time.realtimeSinceStartup - _stateReceivedAt));
                    default:
                        return 0;
                }
            }
        }

        /// <summary>The extraction point the player is extracting at, or null.</summary>
        public string ExtractionPoint { get; private set; }

        public bool IsExtracting => ExtractionPoint != null;

        /// <summary>Seconds until the player extracts, while <see cref="IsExtracting"/>.</summary>
        public float ExtractionSecondsLeft =>
            IsExtracting ? Mathf.Max(0, _extractionLeftAtReceipt - (Time.realtimeSinceStartup - _extractionReceivedAt)) : 0;

        /// <summary>How the player left this raid, once they have. Cleared when a new connection starts.</summary>
        public RaidExit? Exit { get; private set; }

        public event Action<RaidPhase> PhaseChanged;

        /// <summary>The player is out (extracted, died, missing in action, or the raid was aborted). The server disconnects them shortly.</summary>
        public event Action<RaidExit> LeftRaid;

        /// <summary>The extraction countdown started, started over, or stopped.</summary>
        public event Action ExtractionChanged;

        private void Awake() => Instance = this;

        private void Start()
        {
            _networkManager = GetComponent<NetworkManager>();
            _networkManager.ClientManager.RegisterBroadcast<RaidStateBroadcast>(OnState);
            _networkManager.ClientManager.RegisterBroadcast<LeftRaidBroadcast>(OnLeft);
            _networkManager.ClientManager.RegisterBroadcast<ExtractionBroadcast>(OnExtraction);
            _networkManager.ClientManager.OnClientConnectionState += OnClientConnectionState;
        }

        private void OnDestroy()
        {
            if (_networkManager != null)
            {
                _networkManager.ClientManager.UnregisterBroadcast<RaidStateBroadcast>(OnState);
                _networkManager.ClientManager.UnregisterBroadcast<LeftRaidBroadcast>(OnLeft);
                _networkManager.ClientManager.UnregisterBroadcast<ExtractionBroadcast>(OnExtraction);
                _networkManager.ClientManager.OnClientConnectionState -= OnClientConnectionState;
            }

            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void OnState(RaidStateBroadcast message, Channel channel)
        {
            _remainingAtReceipt = message.SecondsRemaining;
            _stateReceivedAt = Time.realtimeSinceStartup;
            Duration = message.Duration;
            if (message.Phase != Phase)
            {
                Phase = message.Phase;
                PhaseChanged?.Invoke(Phase);
            }
        }

        private void OnLeft(LeftRaidBroadcast message, Channel channel)
        {
            Exit = message.Exit;
            SetExtraction(null, 0);
            LeftRaid?.Invoke(message.Exit);
        }

        private void OnExtraction(ExtractionBroadcast message, Channel channel) => SetExtraction(message.Point, message.SecondsLeft);

        private void SetExtraction(string point, float secondsLeft)
        {
            var changed = point != ExtractionPoint || point != null;
            ExtractionPoint = point;
            _extractionLeftAtReceipt = secondsLeft;
            _extractionReceivedAt = Time.realtimeSinceStartup;
            if (changed)
            {
                ExtractionChanged?.Invoke();
            }
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Starting)
            {
                Phase = RaidPhase.Waiting;
                Duration = 0;
                Exit = null;
                SetExtraction(null, 0);
            }
            else if (args.ConnectionState == LocalConnectionState.Stopped)
            {
                SetExtraction(null, 0);
            }
        }
    }
}
