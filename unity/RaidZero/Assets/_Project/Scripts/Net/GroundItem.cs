using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using RaidZero.Items;
using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>
    /// An item lying on the ground in a raid: one pile in the raid's inventory, holding one item (with whatever
    /// is inside it, such as a rifle's magazine). The server spawns one when a player drops something or when
    /// a <see cref="LooseLootSpot"/> rolls an item at raid start, and despawns it when someone takes the item.
    ///
    /// It carries the pile's ID, the item's ID and its type to every client, including ones that join later,
    /// so the client can draw it, name it in the "Take" prompt and ask to pick it up. Its look is the item's
    /// world model, or, while an item has none, the placeholder box sized to the item's inventory footprint.
    /// It's on the DroppedItem prefab, which must stay in the NetworkManager's spawnable prefabs.
    /// </summary>
    public sealed class GroundItem : NetworkBehaviour
    {
        /// <summary>The smallest box the interaction ray can hit, in metres, so a single round can still be aimed at.</summary>
        private const float MinTargetSize = 0.3f;

        private static readonly List<GroundItem> Items = new List<GroundItem>();

        [Tooltip("Shown, sized to the item's inventory footprint, when the item has no world model.")]
        [SerializeField]
        private Transform _placeholder = null;

        [Tooltip("How long one inventory cell is on the placeholder, in metres.")]
        [SerializeField]
        private float _metresPerCell = 0.1f;

        private readonly SyncVar<string> _pileId = new SyncVar<string>();
        private readonly SyncVar<string> _itemId = new SyncVar<string>();
        private readonly SyncVar<string> _defId = new SyncVar<string>();

        private bool _built;

        /// <summary>Every ground item in the scene, on the server and on clients.</summary>
        public static IReadOnlyList<GroundItem> All => Items;

        /// <summary>The pile in the raid's inventory: the container ID for opening it and for reach checks.</summary>
        public string PileId => _pileId.Value;

        /// <summary>The item lying here, for PickUp.</summary>
        public string ItemId => _itemId.Value;

        public string DefId => _defId.Value;

        public string DisplayName => ItemLibrary.DisplayNameOf(DefId);

        /// <summary>Server only, before spawning it: what it is.</summary>
        public void Setup(string pileId, string itemId, string defId)
        {
            _pileId.Value = pileId;
            _itemId.Value = itemId;
            _defId.Value = defId;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            ContainerRegistry.Register(PileId, transform);
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            ContainerRegistry.Unregister(PileId, transform);
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            BuildLook();
        }

        private void OnEnable() => Items.Add(this);

        private void OnDisable() => Items.Remove(this);

        // The values normally arrive with the spawn, before OnStartClient; this covers them arriving after.
        private void Update()
        {
            if (!_built && IsClientInitialized)
            {
                BuildLook();
            }
        }

        private void BuildLook()
        {
            if (_built || string.IsNullOrEmpty(DefId))
            {
                return;
            }

            _built = true;
            var asset = ItemLibrary.Instance != null ? ItemLibrary.Instance.Find(DefId) : null;
            Transform look;
            if (asset != null && asset.WorldPrefab != null)
            {
                look = Instantiate(asset.WorldPrefab, transform).transform;
                look.localPosition = Vector3.zero;
                look.localRotation = Quaternion.identity;
                if (_placeholder != null)
                {
                    _placeholder.gameObject.SetActive(false);
                }
            }
            else if (_placeholder != null)
            {
                look = _placeholder;
                var cells = asset != null ? asset.Size : Vector2Int.one;
                SizePlaceholder(look, cells);
            }
            else
            {
                Debug.LogWarning("[ground] " + name + " has no placeholder and " + DefId + " has no world model, so it's invisible.", this);
                return;
            }

            // Nobody should trip over a magazine: the look's own colliders go, and one trigger box on the root
            // is what the interaction ray finds.
            foreach (var part in look.GetComponentsInChildren<Collider>())
            {
                part.enabled = false;
            }

            if (!TryGetBounds(look, out var bounds))
            {
                bounds = new Bounds(transform.position, Vector3.one * MinTargetSize);
            }

            // Sit it on the ground: the root is where the server put it, on the surface below.
            look.position += Vector3.up * (transform.position.y - bounds.min.y);
            bounds.center += Vector3.up * (transform.position.y - bounds.min.y);

            var box = gameObject.GetComponent<BoxCollider>();
            if (box == null)
            {
                box = gameObject.AddComponent<BoxCollider>();
            }

            box.isTrigger = true;
            box.center = transform.InverseTransformPoint(bounds.center);
            var size = transform.InverseTransformVector(bounds.size);
            box.size = new Vector3(
                Mathf.Max(Mathf.Abs(size.x), MinTargetSize),
                Mathf.Max(Mathf.Abs(size.y), MinTargetSize),
                Mathf.Max(Mathf.Abs(size.z), MinTargetSize));
        }

        // A flat box: the item's width and height in cells, lying down, and not much taller than it needs to be.
        private void SizePlaceholder(Transform placeholder, Vector2Int cells)
        {
            var renderer = placeholder.GetComponentInChildren<Renderer>();
            var meshSize = renderer != null ? renderer.localBounds.size : Vector3.one;
            var want = new Vector3(
                Mathf.Max(1, cells.x) * _metresPerCell,
                Mathf.Clamp(Mathf.Min(cells.x, cells.y) * _metresPerCell * 0.5f, 0.03f, 0.15f),
                Mathf.Max(1, cells.y) * _metresPerCell);
            placeholder.localScale = new Vector3(
                want.x / Mathf.Max(meshSize.x, 0.001f),
                want.y / Mathf.Max(meshSize.y, 0.001f),
                want.z / Mathf.Max(meshSize.z, 0.001f));
        }

        private static bool TryGetBounds(Transform look, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (var renderer in look.GetComponentsInChildren<Renderer>())
            {
                if (!any)
                {
                    bounds = renderer.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return any;
        }
    }
}
