using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>
    /// A dead player's body, lying where they fell, holding everything they carried. The server spawns one when
    /// a player dies (<see cref="RaidServer.PlayerDied"/>) and it stays until the raid ends, even once it's been
    /// emptied. Anyone can search it with F; the inventory screen shows its slots, rig and backpack like a crate.
    ///
    /// It has to be an object of its own: the dead player is disconnected straight after, and Fish-Net despawns
    /// whatever their connection owned, their character included. AI bodies will use it too.
    ///
    /// Its container ID is the dead player's character container's, which the raid's inventory turns into a body
    /// (RaidInventoryService.Kill). It's on the Body prefab, which must stay in the NetworkManager's spawnable
    /// prefabs. It's anonymous: the prompt says "Search body", not whose.
    /// </summary>
    public sealed class Body : NetworkBehaviour
    {
        private static readonly List<Body> Bodies = new List<Body>();

        [Tooltip("Shown, stretched to Size, until there's a character model to lay down instead.")]
        [SerializeField]
        private Transform _placeholder = null;

        [Tooltip("The placeholder's size lying down, in metres: across, up, along.")]
        [SerializeField]
        private Vector3 _size = new Vector3(0.5f, 0.3f, 1.8f);

        private readonly SyncVar<string> _containerId = new SyncVar<string>();

        private bool _built;

        /// <summary>Every body in the scene, on the server and on clients.</summary>
        public static IReadOnlyList<Body> All => Bodies;

        /// <summary>The body's container in the raid's inventory: what opening it asks for.</summary>
        public string ContainerId => _containerId.Value;

        /// <summary>Server only, before spawning it.</summary>
        public void Setup(string containerId) => _containerId.Value = containerId;

        public override void OnStartServer()
        {
            base.OnStartServer();
            ContainerRegistry.Register(ContainerId, transform);
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            ContainerRegistry.Unregister(ContainerId, transform);
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            BuildLook();
        }

        private void OnEnable() => Bodies.Add(this);

        private void OnDisable() => Bodies.Remove(this);

        private void Update()
        {
            if (!_built && IsClientInitialized)
            {
                BuildLook();
            }
        }

        // Lying flat along the body's forward, sitting on the ground at the root. Its own colliders go so nobody
        // trips over it; one trigger box on the root is what the interaction ray finds.
        private void BuildLook()
        {
            if (_built)
            {
                return;
            }

            _built = true;
            if (_placeholder != null)
            {
                var renderer = _placeholder.GetComponentInChildren<Renderer>();
                var meshSize = renderer != null ? renderer.localBounds.size : Vector3.one;
                _placeholder.localScale = new Vector3(
                    _size.x / Mathf.Max(meshSize.x, 0.001f),
                    _size.y / Mathf.Max(meshSize.y, 0.001f),
                    _size.z / Mathf.Max(meshSize.z, 0.001f));
                foreach (var part in _placeholder.GetComponentsInChildren<Collider>())
                {
                    part.enabled = false;
                }

                if (renderer != null)
                {
                    _placeholder.position += Vector3.up * (transform.position.y - renderer.bounds.min.y);
                }
            }

            var box = gameObject.GetComponent<BoxCollider>();
            if (box == null)
            {
                box = gameObject.AddComponent<BoxCollider>();
            }

            box.isTrigger = true;
            box.center = new Vector3(0f, _size.y / 2f, 0f);
            box.size = new Vector3(Mathf.Max(_size.x, 0.6f), Mathf.Max(_size.y, 0.4f), _size.z);
        }
    }
}
