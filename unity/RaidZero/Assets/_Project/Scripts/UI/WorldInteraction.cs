using RaidZero.Items;
using RaidZero.Net;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Raid;
using UnityEngine;
using UnityEngine.InputSystem;

namespace RaidZero.UI
{
    /// <summary>
    /// What the player is looking at in a raid, and F to use it: take an item lying on the ground
    /// (<see cref="GroundItem"/>), open a crate (<see cref="LootContainer"/>) or search a body
    /// (<see cref="Body"/>). <see cref="RaidHud"/> shows the prompt ("F  Take AK-74") under the middle of the
    /// screen. Crates and bodies open in the inventory screen.
    ///
    /// Taking puts the item straight onto the player, into the first place it fits (a rifle into an empty
    /// primary, then their rig and backpack). If there's no room, the item's pile opens in the inventory screen
    /// so they can make some, or drag it where they want it. The server checks reach and room either way.
    ///
    /// Goes next to the inventory screen and the HUD, on the client's persistent object in core.client.
    /// </summary>
    public sealed class WorldInteraction : MonoBehaviour
    {
        private const float MessageSeconds = 3f;

        [Tooltip("How far from the camera the player can reach, in metres. Keep it within RaidServer's reach distance, or the server will refuse what the prompt offered.")]
        [SerializeField]
        private float _reach = 2.5f;

        private InventoryScreen _screen;
        private RaidInventoryClient _hooked;
        private uint _takeRequest;
        private string _takePile;
        private string _takeName;
        private string _message;
        private float _messageUntil;

        public static WorldInteraction Instance { get; private set; }

        /// <summary>What F would do now, such as "Take AK-74"; null when there's nothing to use.</summary>
        public string Prompt { get; private set; }

        /// <summary>A short sentence about the last thing F did, such as "That's out of reach"; null when there's none.</summary>
        public string Message => Time.unscaledTime < _messageUntil ? _message : null;

        private void Awake() => Instance = this;

        private void OnDestroy()
        {
            Hook(null);
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Update()
        {
            Prompt = null;
            var client = RaidInventoryClient.Instance;
            Hook(client);
            if (client == null || client.View.Character == null)
            {
                return; // not in a raid
            }

            if (_screen == null)
            {
                _screen = FindAnyObjectByType<InventoryScreen>();
            }

            if ((_screen != null && _screen.IsOpen) || EscapeMenu.IsShowing)
            {
                return;
            }

            var camera = Camera.main;
            var target = camera != null ? FindTarget(camera.transform) : null;
            if (target == null)
            {
                return;
            }

            var item = target.GetComponentInParent<GroundItem>();
            var body = item == null ? target.GetComponentInParent<Body>() : null;
            var crate = item == null && body == null ? target.GetComponentInParent<LootContainer>() : null;
            Prompt = item != null ? "Take " + item.DisplayName
                : body != null ? "Search body"
                : "Open " + ItemLibrary.DisplayNameOf(crate.DefId);

            var keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.fKey.wasPressedThisFrame)
            {
                return;
            }

            if (item != null)
            {
                Take(client, item);
            }
            else
            {
                client.Open(body != null ? body.ContainerId : crate.ContainerId);
                if (_screen != null)
                {
                    _screen.Open();
                }
            }
        }

        private void Take(RaidInventoryClient client, GroundItem item)
        {
            if (client.View.IsPending(item.ItemId))
            {
                return; // already asked
            }

            client.PickUp(item.ItemId);
            _takeRequest = client.View.LastRequestId;
            _takePile = item.PileId;
            _takeName = item.DisplayName;
        }

        // The answer to a Take: nothing to do on success (the item shows up on the player and its GroundItem
        // goes); with no room, open the pile so they can make some.
        private void OnRequestCompleted(RequestResult result)
        {
            if (result.RequestId != _takeRequest || _takePile == null)
            {
                return;
            }

            var pile = _takePile;
            _takePile = null;
            if (result.Success)
            {
                return;
            }

            if (result.Error == InventoryErrorCodes.NoSpace && _hooked != null)
            {
                _hooked.Open(pile);
                if (_screen != null)
                {
                    _screen.Open();
                    _screen.ShowMessage("There's no room on you for the " + _takeName + ". Make some, or drag it where you want it.");
                }

                return;
            }

            Say(result.Error == RaidInventoryErrorCodes.OutOfReach ? "That's out of reach." : "You couldn't take the " + _takeName + ".");
        }

        private void Say(string sentence)
        {
            _message = sentence;
            _messageUntil = Time.unscaledTime + MessageSeconds;
        }

        // The client is created by the client setup and replaced if the scene restarts; follow it.
        private void Hook(RaidInventoryClient client)
        {
            if (_hooked == client)
            {
                return;
            }

            if (_hooked != null)
            {
                _hooked.View.RequestCompleted -= OnRequestCompleted;
            }

            _hooked = client;
            _takePile = null;
            if (_hooked != null)
            {
                _hooked.View.RequestCompleted += OnRequestCompleted;
            }
        }

        /// <summary>
        /// The ground item, body or crate straight ahead within reach, or null. Solid things in the way block it;
        /// triggers that aren't ground items (extraction zones, say) don't. Ground items are triggers so
        /// nobody trips over them, which is why this looks at everything the ray passes through.
        /// </summary>
        private Collider FindTarget(Transform eye)
        {
            var hits = Physics.RaycastAll(eye.position, eye.forward, _reach, ~0, QueryTriggerInteraction.Collide);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                var collider = hit.collider;
                if (collider.GetComponentInParent<GroundItem>() != null || collider.GetComponentInParent<Body>() != null
                    || collider.GetComponentInParent<LootContainer>() != null)
                {
                    return collider;
                }

                if (!collider.isTrigger && !IsLocalPlayer(collider))
                {
                    return null; // a wall, a table, another player
                }
            }

            return null;
        }

        // The camera sits inside the player's own body; don't let it block the ray.
        private static bool IsLocalPlayer(Collider collider)
        {
            var body = collider.GetComponentInParent<FishNet.Object.NetworkObject>();
            return body != null && body.IsOwner;
        }
    }
}
