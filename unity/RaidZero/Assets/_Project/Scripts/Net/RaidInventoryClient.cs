using System.Collections.Generic;
using FishNet.Managing;
using FishNet.Transporting;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.InventoryUi;
using RaidZero.Shared.Raid;
using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>
    /// The client's end of the raid inventory: carries <see cref="View"/>'s requests to the server and the
    /// server's views back. Goes on the NetworkManager's GameObject. The inventory screen reads
    /// <see cref="View"/> (its containers, what's pending, and its events) and calls the methods here.
    /// </summary>
    [RequireComponent(typeof(NetworkManager))]
    public sealed class RaidInventoryClient : MonoBehaviour
    {
        private NetworkManager _networkManager;

        public static RaidInventoryClient Instance { get; private set; }

        /// <summary>What the player can see: their gear and whatever they have open. Updated by the server only.</summary>
        public RaidInventoryView View { get; } = new RaidInventoryView();

        /// <summary>The inventory screen's source for this raid. Dispose it when the screen goes away.</summary>
        public RaidInventorySource CreateSource() => new RaidInventorySource(View, Send);

        /// <summary>Move, split or merge as one all-or-nothing batch. Grey out View.IsPending items until answered.</summary>
        public void Request(IReadOnlyList<InventoryOp> ops) => Send(View.Request(ops));

        public void PickUp(string itemId) => Send(View.PickUp(itemId));

        public void Drop(string itemId) => Send(View.Drop(itemId));

        /// <summary>Start looting a container, such as a LootContainer's ContainerId. The server checks the distance.</summary>
        public void Open(string containerId) => Send(View.Open(containerId));

        public void Close(string containerId) => Send(View.Close(containerId));

        private void Awake() => Instance = this;

        private void Start()
        {
            _networkManager = GetComponent<NetworkManager>();
            _networkManager.ClientManager.RegisterBroadcast<InventoryToClientBroadcast>(OnMessage);
            _networkManager.ClientManager.OnClientConnectionState += OnClientConnectionState;
        }

        private void OnDestroy()
        {
            if (_networkManager != null)
            {
                _networkManager.ClientManager.UnregisterBroadcast<InventoryToClientBroadcast>(OnMessage);
                _networkManager.ClientManager.OnClientConnectionState -= OnClientConnectionState;
            }

            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Send(byte[] data) => _networkManager.ClientManager.Broadcast(new InventoryToServerBroadcast { Data = data });

        private void OnMessage(InventoryToClientBroadcast message, Channel channel)
        {
            try
            {
                View.Receive(message.Data);
            }
            catch (ProtocolException exception)
            {
                // Only possible if the client and server builds speak different protocol versions.
                Debug.LogError("[raid] The server sent an inventory message this build can't read: " + exception.Message);
            }
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Stopped)
            {
                View.Clear();
            }
        }
    }
}
