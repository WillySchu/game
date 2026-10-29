using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.InventoryUi;

namespace RaidZero.BackendApi
{
    /// <summary>
    /// The inventory screen's source in the Hideout: the player's gear and stash from <see cref="PlayerSession"/>,
    /// edited over HTTP. The Hideout's counterpart to the raid's RaidInventorySource, so the same screen (and
    /// InventoryModel's drop rules) works in both places.
    ///
    /// An edit marks its items pending until the backend answers; the session checks it locally first, sends it
    /// with the profile version, and on a version conflict refreshes and tries once more. A refusal arrives as
    /// <see cref="Refused"/> with its code (in_raid while the stash is locked for a raid, say), and the profile
    /// the screen shows is always the backend's latest. Nothing can be dropped here: there's no ground.
    ///
    /// Use it from the main thread, as the session is. Dispose it when the screen goes away.
    /// </summary>
    public sealed class HideoutInventorySource : IInventorySource, IDisposable
    {
        private readonly PlayerSession _session;
        private readonly List<InventoryRoot> _roots = new List<InventoryRoot>();
        private readonly Dictionary<string, int> _pending = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly object _gate = new object();
        private int _inFlight;
        private TaskCompletionSource<bool> _settled;

        public HideoutInventorySource(PlayerSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _session.ProfileChanged += OnProfileChanged;
            _session.LoggedOut += OnLoggedOut;
            Rebuild(_session.Profile);
        }

        public event Action Changed;

        public event Action<string> Refused;

        /// <summary>The character (their gear) first, then the stash. Empty until the session has a profile.</summary>
        public IReadOnlyList<InventoryRoot> Roots => _roots;

        /// <summary>There's no ground to drop things on in the Hideout.</summary>
        public bool CanDrop => false;

        /// <summary>Done when every edit sent so far has been answered: for a "saving" indicator, and for tests.</summary>
        public Task Settled
        {
            get
            {
                lock (_gate)
                {
                    return _inFlight == 0 ? Task.CompletedTask : _settled.Task;
                }
            }
        }

        public bool IsPending(string itemId)
        {
            lock (_gate)
            {
                return itemId != null && _pending.ContainsKey(itemId);
            }
        }

        public void Send(IReadOnlyList<InventoryOp> ops)
        {
            if (ops == null || ops.Count == 0)
            {
                return;
            }

            var touched = Touched(ops);
            lock (_gate)
            {
                foreach (var id in touched)
                {
                    _pending[id] = (_pending.TryGetValue(id, out var count) ? count : 0) + 1;
                }

                if (_inFlight++ == 0)
                {
                    _settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            Changed?.Invoke(); // its items are pending now
            _ = EditAsync(ops, touched);
        }

        /// <summary>Not in the Hideout (<see cref="CanDrop"/> is false, so the screen never asks).</summary>
        public void Drop(string itemId) => Refused?.Invoke(InventoryUiErrors.CantDrop);

        /// <summary>The character and stash can't be closed.</summary>
        public void Close(string rootId)
        {
        }

        public void Dispose()
        {
            _session.ProfileChanged -= OnProfileChanged;
            _session.LoggedOut -= OnLoggedOut;
        }

        private async Task EditAsync(IReadOnlyList<InventoryOp> ops, List<string> touched)
        {
            string error;
            try
            {
                var result = await _session.ApplyAsync(ops);
                error = result.Error;
            }
            catch (Exception)
            {
                error = "backend_unreachable";
            }

            TaskCompletionSource<bool> settled = null;
            lock (_gate)
            {
                foreach (var id in touched)
                {
                    if (_pending.TryGetValue(id, out var count))
                    {
                        if (count <= 1)
                        {
                            _pending.Remove(id);
                        }
                        else
                        {
                            _pending[id] = count - 1;
                        }
                    }
                }

                if (--_inFlight == 0)
                {
                    settled = _settled;
                }
            }

            if (error != null)
            {
                Refused?.Invoke(error);
            }

            Changed?.Invoke(); // not pending any more; the profile itself arrived through ProfileChanged
            settled?.TrySetResult(true);
        }

        private void OnProfileChanged(PlayerProfile profile)
        {
            Rebuild(profile);
            Changed?.Invoke();
        }

        private void OnLoggedOut()
        {
            Rebuild(null);
            Changed?.Invoke();
        }

        private void Rebuild(PlayerProfile profile)
        {
            _roots.Clear();
            if (profile == null)
            {
                return;
            }

            if (profile.Character != null)
            {
                _roots.Add(new InventoryRoot(InventoryRootKind.Character, profile.Character));
            }

            if (profile.Stash != null)
            {
                _roots.Add(new InventoryRoot(InventoryRootKind.Stash, profile.Stash));
            }
        }

        // The items an edit is about: what it moves, splits or merges, and what it merges into.
        private static List<string> Touched(IReadOnlyList<InventoryOp> ops)
        {
            var ids = new List<string>();
            foreach (var op in ops)
            {
                if (op.ItemId != null && !ids.Contains(op.ItemId))
                {
                    ids.Add(op.ItemId);
                }

                if (op.IntoId != null && !ids.Contains(op.IntoId))
                {
                    ids.Add(op.IntoId);
                }
            }

            return ids;
        }
    }
}
