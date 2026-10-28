using System;
using System.Collections.Generic;
using System.Linq;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Raid;

namespace RaidZero.Raid
{
    /// <summary>
    /// The server's truth about every item in one raid: each player's gear, every crate, every body and
    /// everything lying on the ground, held as the roots of one <see cref="InventoryTree"/>. Looting, picking
    /// up and dropping are all moves between those roots, so an item can't be in two places or vanish.
    ///
    /// Who can touch what:
    /// - A living player can always rearrange their own gear.
    /// - They can reach into a crate or a body only while they have it open (<see cref="Open"/>), and can
    ///   pick up things lying on the ground. The game decides whether they're close enough before calling
    ///   <see cref="Open"/> or <see cref="PickUp"/>, and calls <see cref="Close"/> when they walk away.
    /// - Nobody can touch another living player's gear. Once a player dies (<see cref="Kill"/>), their gear
    ///   becomes a body anyone can open.
    /// - Nothing is destroyed by players in a raid: discarding is refused, and a drop lands on the ground.
    ///
    /// Every change returns a <see cref="RaidInventoryResult"/> whose updates say which players now need to
    /// see which containers. Not thread-safe: call it from the server's main thread only.
    /// </summary>
    public sealed class RaidInventoryService
    {
        private readonly ItemCatalog _catalog;
        private readonly Func<string> _newId;
        private readonly InventoryTree _tree;
        private readonly Dictionary<string, RootKind> _kinds = new Dictionary<string, RootKind>(StringComparer.Ordinal);
        private readonly Dictionary<string, Player> _players = new Dictionary<string, Player>(StringComparer.Ordinal);

        /// <param name="catalog">Must include the character and ground definitions.</param>
        /// <param name="newId">Mints IDs for splits and ground piles. Defaults to <see cref="Uuid7.NewId"/>.</param>
        public RaidInventoryService(ItemCatalog catalog, Func<string> newId = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            foreach (var required in new[] { WellKnownDefs.Character, WellKnownDefs.Ground })
            {
                if (!catalog.Contains(required))
                {
                    throw new ArgumentException("The catalog has no '" + required + "' definition, which raids need.", nameof(catalog));
                }
            }

            _newId = newId ?? Uuid7.NewId;
            _tree = new InventoryTree(catalog, new ItemNode[0], _newId);
        }

        private enum PlayerState
        {
            Active,
            Dead,
            Extracted,
        }

        public ItemCatalog Catalog => _catalog;

        // ---- Setting up ----

        /// <summary>
        /// A player joined with the loadout the backend gave them (JoinResult.Loadout). The tree takes the
        /// nodes. Returns false if they're already in, which happens on a reconnect; nothing changes then.
        /// </summary>
        public bool AddPlayer(string playerId, ItemNode character)
        {
            if (string.IsNullOrEmpty(playerId))
            {
                throw new ArgumentException("A player ID is required.", nameof(playerId));
            }

            if (_players.TryGetValue(playerId, out var existing))
            {
                if (existing.State != PlayerState.Active)
                {
                    throw new InvalidOperationException("Player " + playerId + " already " + (existing.State == PlayerState.Dead ? "died" : "extracted") + " in this raid.");
                }

                return false;
            }

            if (character == null || character.DefId != WellKnownDefs.Character)
            {
                throw new ArgumentException("A player's loadout must be rooted at their character.", nameof(character));
            }

            _tree.AddRoot(character);
            _kinds[character.Id] = RootKind.Character;
            _players[playerId] = new Player(character.Id);
            return true;
        }

        /// <summary>
        /// Places a loot container (a crate, a filing cabinet) with whatever was rolled into it. Items the
        /// server creates need fresh IDs from <see cref="Uuid7.NewId"/>; they stay the same all the way into
        /// whoever's stash they end up in.
        /// </summary>
        public void AddContainer(ItemNode container)
        {
            if (container == null)
            {
                throw new ArgumentNullException(nameof(container));
            }

            if (container.DefId == WellKnownDefs.Character || container.DefId == WellKnownDefs.Ground)
            {
                throw new ArgumentException("Characters join with AddPlayer, and ground piles come from Drop.", nameof(container));
            }

            if (!_catalog.TryGet(container.DefId, out var def) || (!def.HasGrids() && (def.Slots == null || def.Slots.Count == 0)))
            {
                throw new ArgumentException("'" + container.DefId + "' can't hold anything, so it can't be a loot container.", nameof(container));
            }

            _tree.AddRoot(container);
            _kinds[container.Id] = RootKind.Container;
        }

        // ---- Reading ----

        /// <summary>Whether the player is in the raid and alive.</summary>
        public bool IsActive(string playerId) => FindActive(playerId) != null;

        /// <summary>The ID of the player's character container (their body, once dead). Null if unknown or extracted.</summary>
        public string CharacterOf(string playerId) =>
            playerId != null && _players.TryGetValue(playerId, out var player) && player.State != PlayerState.Extracted ? player.CharacterId : null;

        public RootKind? KindOf(string rootId) => rootId != null && _kinds.TryGetValue(rootId, out var kind) ? kind : (RootKind?)null;

        /// <summary>The ID of the root container an item is in (its own ID for a root), or null.</summary>
        public string RootOf(string itemId) => _tree.RootOf(itemId)?.Id;

        /// <summary>A copy of a root container and everything in it, or null.</summary>
        public ItemNode Snapshot(string rootId) => _kinds.ContainsKey(rootId ?? "") && _tree.TryGet(rootId, out var root) ? root.Clone() : null;

        /// <summary>Everything the player can see: their own gear first, then each container they have open. Copies.</summary>
        public IReadOnlyList<ItemNode> ViewOf(string playerId)
        {
            var player = FindActive(playerId);
            if (player == null)
            {
                return new ItemNode[0];
            }

            var view = new List<ItemNode> { Snapshot(player.CharacterId) };
            view.AddRange(player.Open.Select(Snapshot));
            return view;
        }

        /// <summary>Containers the player has open.</summary>
        public IReadOnlyCollection<string> OpenContainersOf(string playerId) =>
            playerId != null && _players.TryGetValue(playerId, out var player) ? (IReadOnlyCollection<string>)player.Open : new string[0];

        /// <summary>
        /// The live item, for reading only (how many rounds are in a magazine, what's in the primary slot).
        /// Don't modify it, and don't keep it: the next change replaces the node objects.
        /// </summary>
        public ItemNode Find(string itemId) => _tree.TryGet(itemId, out var node) ? node : null;

        // ---- Containers ----

        /// <summary>
        /// The player starts looting a crate, a body or a pile on the ground. Check they're close enough
        /// first. The result's update carries the container's contents to them.
        /// </summary>
        public RaidInventoryResult Open(string playerId, string containerId)
        {
            var player = FindActive(playerId);
            if (player == null)
            {
                return NotActive(playerId);
            }

            var kind = KindOf(containerId);
            if (kind == null)
            {
                return RaidInventoryResult.Failed(new InventoryError(containerId, InventoryErrorCodes.TargetNotFound, "No container '" + containerId + "'."));
            }

            if (kind == RootKind.Character)
            {
                return RaidInventoryResult.Failed(new InventoryError(containerId, RaidInventoryErrorCodes.NotAllowed, "Another living player's gear can't be opened."));
            }

            player.Open.Add(containerId);
            return RaidInventoryResult.Succeeded(null, new[] { new RootUpdate(playerId, containerId, Snapshot(containerId)) }, null, null);
        }

        /// <summary>The player stopped looting it, or walked out of reach.</summary>
        public void Close(string playerId, string containerId)
        {
            if (playerId != null && _players.TryGetValue(playerId, out var player))
            {
                player.Open.Remove(containerId);
            }
        }

        // ---- Player requests ----

        /// <summary>
        /// A player's own inventory request: move, split or merge, in one all-or-nothing batch. Every item and
        /// target must be in their gear or in a container they have open, so this covers rearranging gear,
        /// looting (moving out of an open crate or body) and stashing things into one.
        /// </summary>
        public RaidInventoryResult Apply(string playerId, IReadOnlyList<InventoryOp> ops)
        {
            if (ops == null)
            {
                throw new ArgumentNullException(nameof(ops));
            }

            var player = FindActive(playerId);
            if (player == null)
            {
                return NotActive(playerId);
            }

            var touched = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < ops.Count; i++)
            {
                var op = ops[i];
                if (op?.Op == InventoryOpKinds.Discard)
                {
                    return RaidInventoryResult.Failed(new InventoryError(op.ItemId, RaidInventoryErrorCodes.NotAllowed, "Items can't be destroyed in a raid; drop them instead."), i);
                }

                foreach (var id in new[] { op?.ItemId, op?.To?.ParentId, op?.IntoId })
                {
                    var root = _tree.RootOf(id);
                    if (root == null)
                    {
                        continue; // Unknown IDs fail inside the tree with the usual not-found codes.
                    }

                    if (!CanReach(player, root.Id))
                    {
                        return RaidInventoryResult.Failed(new InventoryError(id, RaidInventoryErrorCodes.OutOfReach, "That's not within reach."), i);
                    }

                    touched.Add(root.Id);
                }
            }

            var applied = _tree.Apply(ops);
            return applied.Success ? Finish(applied, touched, null) : RaidInventoryResult.Failed(applied.Error, applied.FailedOpIndex);
        }

        /// <summary>
        /// "Take": moves an item from the ground or an open container onto the player, into the first place
        /// it fits. That's an empty slot made for it (a rifle into an empty primary), then the grids of what
        /// they wear, in slot order (rig before backpack). Check they're close enough first.
        /// </summary>
        public RaidInventoryResult PickUp(string playerId, string itemId)
        {
            var player = FindActive(playerId);
            if (player == null)
            {
                return NotActive(playerId);
            }

            var root = _tree.RootOf(itemId);
            if (root == null || root.Id == itemId)
            {
                return RaidInventoryResult.Failed(new InventoryError(itemId, InventoryErrorCodes.ItemNotFound, "No item '" + itemId + "' to pick up."));
            }

            if (root.Id == player.CharacterId)
            {
                return RaidInventoryResult.Failed(new InventoryError(itemId, RaidInventoryErrorCodes.NotAllowed, "That's already on you."));
            }

            if (!CanReach(player, root.Id) && KindOf(root.Id) != RootKind.Ground)
            {
                return RaidInventoryResult.Failed(new InventoryError(itemId, RaidInventoryErrorCodes.OutOfReach, "That's not within reach."));
            }

            _tree.TryGet(itemId, out var item);
            foreach (var target in PickUpTargets(player, item))
            {
                var applied = _tree.Apply(InventoryOp.Move(itemId, target));
                if (applied.Success)
                {
                    return Finish(applied, new HashSet<string>(StringComparer.Ordinal) { root.Id, player.CharacterId }, null);
                }
            }

            return RaidInventoryResult.Failed(new InventoryError(itemId, InventoryErrorCodes.NoSpace, "There's no room on you for that."));
        }

        /// <summary>
        /// The player drops one of their items. It lands in a new pile on the ground; the result's
        /// <see cref="RaidInventoryResult.CreatedRoots"/> has the pile's ID, to spawn a pickup where they stand.
        /// </summary>
        public RaidInventoryResult Drop(string playerId, string itemId)
        {
            var player = FindActive(playerId);
            if (player == null)
            {
                return NotActive(playerId);
            }

            var root = _tree.RootOf(itemId);
            if (root == null || root.Id == itemId || root.Id != player.CharacterId)
            {
                return RaidInventoryResult.Failed(new InventoryError(itemId, InventoryErrorCodes.ItemNotFound, "You aren't carrying '" + itemId + "'."));
            }

            var pile = new ItemNode { Id = _newId(), DefId = WellKnownDefs.Ground };
            _tree.AddRoot(pile);
            _kinds[pile.Id] = RootKind.Ground;

            var applied = _tree.Apply(InventoryOp.Move(itemId, InventoryTarget.AnyGridSpot(pile.Id)));
            if (!applied.Success)
            {
                _tree.RemoveRoot(pile.Id);
                _kinds.Remove(pile.Id);
                return RaidInventoryResult.Failed(applied.Error);
            }

            return Finish(applied, new HashSet<string>(StringComparer.Ordinal) { player.CharacterId, pile.Id }, new[] { pile.Id });
        }

        // ---- Server decisions ----

        /// <summary>
        /// Uses up part of one of the player's items: a round per shot from the loaded magazine, a medkit.
        /// Fails, and changes nothing, if they don't have that many.
        /// </summary>
        public RaidInventoryResult Consume(string playerId, string itemId, int quantity)
        {
            var player = FindActive(playerId);
            if (player == null)
            {
                return NotActive(playerId);
            }

            var root = _tree.RootOf(itemId);
            if (root == null || root.Id != player.CharacterId)
            {
                return RaidInventoryResult.Failed(new InventoryError(itemId, InventoryErrorCodes.ItemNotFound, "You aren't carrying '" + itemId + "'."));
            }

            var applied = _tree.Consume(itemId, quantity);
            return applied.Success
                ? Finish(applied, new HashSet<string>(StringComparer.Ordinal) { player.CharacterId }, null)
                : RaidInventoryResult.Failed(applied.Error);
        }

        /// <summary>
        /// The player died. Their gear stays where it is, as a body anyone can open; they can't act any more.
        /// Report the death to the backend separately (BackendClient.ReportDied).
        /// </summary>
        public void Kill(string playerId)
        {
            var player = FindActive(playerId) ?? throw new InvalidOperationException("Player " + playerId + " isn't alive in this raid.");
            player.State = PlayerState.Dead;
            player.Open.Clear();
            _kinds[player.CharacterId] = RootKind.Body;
        }

        /// <summary>
        /// The player extracted. Returns their character container with everything on it, for
        /// BackendClient.ReportExtracted, and takes it out of the raid.
        /// </summary>
        public ItemNode Extract(string playerId)
        {
            var player = FindActive(playerId) ?? throw new InvalidOperationException("Player " + playerId + " isn't alive in this raid.");
            player.State = PlayerState.Extracted;
            player.Open.Clear();
            _kinds.Remove(player.CharacterId);
            return _tree.RemoveRoot(player.CharacterId);
        }

        // ---- Internals ----

        private static RaidInventoryResult NotActive(string playerId) =>
            RaidInventoryResult.Failed(new InventoryError(null, RaidInventoryErrorCodes.PlayerNotActive, "Player " + playerId + " isn't alive in this raid."));

        private Player FindActive(string playerId) =>
            playerId != null && _players.TryGetValue(playerId, out var player) && player.State == PlayerState.Active ? player : null;

        private static bool CanReach(Player player, string rootId) => rootId == player.CharacterId || player.Open.Contains(rootId);

        /// <summary>Empty slots meant for the item first, then the grids of whatever the player wears, in slot order.</summary>
        private IEnumerable<InventoryTarget> PickUpTargets(Player player, ItemNode item)
        {
            _tree.TryGet(player.CharacterId, out var character);
            var characterDef = _catalog.Get(WellKnownDefs.Character);
            _catalog.TryGet(item.DefId, out var itemDef);
            var tags = itemDef?.Tags ?? new List<string>();

            foreach (var slot in characterDef.Slots)
            {
                bool empty = character.Children.All(c => c.Slot != slot.Name);
                if (empty && slot.Accepts.Any(tags.Contains))
                {
                    yield return InventoryTarget.NamedSlot(character.Id, slot.Name);
                }
            }

            foreach (var slot in characterDef.Slots)
            {
                var worn = character.Children.FirstOrDefault(c => c.Slot == slot.Name);
                if (worn != null && _catalog.TryGet(worn.DefId, out var wornDef) && wornDef.HasGrids())
                {
                    yield return InventoryTarget.AnyGridSpot(worn.Id);
                }
            }
        }

        /// <summary>
        /// After a successful change: removes ground piles that are now empty, then works out who needs to
        /// see which of the touched containers.
        /// </summary>
        private RaidInventoryResult Finish(ApplyResult applied, HashSet<string> touched, IReadOnlyList<string> createdRoots)
        {
            var removed = new List<string>();
            foreach (var rootId in touched)
            {
                if (KindOf(rootId) == RootKind.Ground && _tree.TryGet(rootId, out var pile) && pile.Children.Count == 0)
                {
                    _tree.RemoveRoot(rootId);
                    _kinds.Remove(rootId);
                    removed.Add(rootId);
                }
            }

            var updates = new List<RootUpdate>();
            foreach (var rootId in touched)
            {
                var snapshot = removed.Contains(rootId) ? null : Snapshot(rootId);
                foreach (var entry in _players)
                {
                    var player = entry.Value;
                    bool sees = player.State == PlayerState.Active && (player.CharacterId == rootId || player.Open.Contains(rootId));
                    if (!sees)
                    {
                        continue;
                    }

                    updates.Add(new RootUpdate(entry.Key, rootId, snapshot));
                    if (snapshot == null)
                    {
                        player.Open.Remove(rootId);
                    }
                }
            }

            return RaidInventoryResult.Succeeded(applied, updates, createdRoots, removed);
        }

        private sealed class Player
        {
            public Player(string characterId)
            {
                CharacterId = characterId;
            }

            public string CharacterId { get; }

            public PlayerState State { get; set; }

            public HashSet<string> Open { get; } = new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
