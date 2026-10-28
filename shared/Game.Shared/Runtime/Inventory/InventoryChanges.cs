using System.Collections.Generic;

namespace RaidZero.Shared.Inventory
{
    /// <summary>Why an item left the tree.</summary>
    public enum RemovalReason
    {
        /// <summary>The player threw it away.</summary>
        Discarded,

        /// <summary>Its whole quantity moved into another stack.</summary>
        Merged,

        /// <summary>Used up in a raid: rounds fired, a medkit used. Only the game server does this.</summary>
        Consumed,
    }

    /// <summary>
    /// The net effect of a batch of operations, by item ID. The backend turns this into row updates and
    /// ledger entries; the game server turns it into a diff for the owning client.
    ///
    /// "Net" means an item created and then merged away in the same batch appears nowhere, and an item
    /// that was updated and then removed appears only as removed.
    /// </summary>
    public sealed class ChangeSet
    {
        private readonly List<string> _created = new List<string>();
        private readonly List<string> _updated = new List<string>();
        private readonly Dictionary<string, RemovalReason> _removed = new Dictionary<string, RemovalReason>();

        /// <summary>New items, in the order they were created.</summary>
        public IReadOnlyList<string> Created => _created;

        /// <summary>Existing items whose position, quantity or parent changed.</summary>
        public IReadOnlyList<string> Updated => _updated;

        /// <summary>Items that no longer exist in the tree, including every descendant of a discarded container.</summary>
        public IReadOnlyDictionary<string, RemovalReason> Removed => _removed;

        public bool IsEmpty => _created.Count == 0 && _updated.Count == 0 && _removed.Count == 0;

        internal void MarkCreated(string id)
        {
            _created.Add(id);
        }

        internal void MarkUpdated(string id)
        {
            if (!_created.Contains(id) && !_updated.Contains(id))
            {
                _updated.Add(id);
            }
        }

        internal void MarkRemoved(string id, RemovalReason reason)
        {
            if (_created.Remove(id))
            {
                return;
            }

            _updated.Remove(id);
            _removed[id] = reason;
        }
    }

    /// <summary>An item a split created, and which op created it, for the API's "created" list.</summary>
    public sealed class CreatedItem
    {
        public CreatedItem(int opIndex, string itemId)
        {
            OpIndex = opIndex;
            ItemId = itemId;
        }

        public int OpIndex { get; }

        public string ItemId { get; }
    }

    public sealed class ApplyResult
    {
        private ApplyResult(bool success, int? failedOpIndex, InventoryError error, IReadOnlyList<CreatedItem> created, ChangeSet changes)
        {
            Success = success;
            FailedOpIndex = failedOpIndex;
            Error = error;
            Created = created;
            Changes = changes;
        }

        public bool Success { get; }

        /// <summary>
        /// The op that was rejected. Null on success, and also null when every op passed its own checks but
        /// the resulting tree failed full validation, which points at bad data in the tree to begin with.
        /// </summary>
        public int? FailedOpIndex { get; }

        /// <summary>Why the batch was rejected. Its Code is the API's "reason". Null on success.</summary>
        public InventoryError Error { get; }

        public IReadOnlyList<CreatedItem> Created { get; }

        /// <summary>Empty when the batch failed: nothing was applied.</summary>
        public ChangeSet Changes { get; }

        internal static ApplyResult Succeeded(IReadOnlyList<CreatedItem> created, ChangeSet changes)
        {
            return new ApplyResult(true, null, null, created, changes);
        }

        internal static ApplyResult Failed(int? opIndex, InventoryError error)
        {
            return new ApplyResult(false, opIndex, error, new List<CreatedItem>(), new ChangeSet());
        }
    }
}
