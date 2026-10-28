using System;
using System.Collections.Generic;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Raid;

namespace RaidZero.Shared.InventoryUi
{
    /// <summary>
    /// The inventory screen's view of a raid: the containers the server says this player can see, with the
    /// character first and the most recently opened container next. Changes go to the game server as
    /// protocol messages, and nothing changes on screen until it answers. In Unity, RaidInventoryClient
    /// makes one with its own way of sending.
    /// </summary>
    public sealed class RaidInventorySource : IInventorySource, IDisposable
    {
        private readonly RaidInventoryView _view;
        private readonly Action<byte[]> _send;
        private readonly List<string> _openedOrder = new List<string>();
        private readonly List<InventoryRoot> _roots = new List<InventoryRoot>();

        /// <param name="send">Sends one encoded message to the game server.</param>
        public RaidInventorySource(RaidInventoryView view, Action<byte[]> send)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _view.ContainerChanged += OnContainerChanged;
            _view.ContainerClosed += OnContainerClosed;
            _view.RequestCompleted += OnRequestCompleted;
            _view.Cleared += OnCleared;
            _openedOrder.AddRange(_view.Containers.Keys);
            Rebuild();
        }

        public event Action Changed;

        public event Action<string> Refused;

        public IReadOnlyList<InventoryRoot> Roots => _roots;

        public bool CanDrop => true;

        public bool IsPending(string itemId) => _view.IsPending(itemId);

        public void Send(IReadOnlyList<InventoryOp> ops)
        {
            _send(_view.Request(ops));
            Changed?.Invoke(); // its items are pending now
        }

        public void Drop(string itemId)
        {
            _send(_view.Drop(itemId));
            Changed?.Invoke();
        }

        public void Close(string rootId) => _send(_view.Close(rootId)); // the view reports it closed

        public void Dispose()
        {
            _view.ContainerChanged -= OnContainerChanged;
            _view.ContainerClosed -= OnContainerClosed;
            _view.RequestCompleted -= OnRequestCompleted;
            _view.Cleared -= OnCleared;
        }

        private void OnContainerChanged(ContainerView view)
        {
            // Something newly visible goes to the front: the crate you just opened is the one quick move targets.
            if (!_openedOrder.Contains(view.Root.Id))
            {
                _openedOrder.Insert(0, view.Root.Id);
            }

            Rebuild();
            Changed?.Invoke();
        }

        private void OnContainerClosed(string containerId)
        {
            _openedOrder.Remove(containerId);
            Rebuild();
            Changed?.Invoke();
        }

        private void OnRequestCompleted(RequestResult result)
        {
            if (!result.Success)
            {
                Refused?.Invoke(result.Error);
            }

            Changed?.Invoke(); // its items aren't pending any more
        }

        private void OnCleared()
        {
            _openedOrder.Clear();
            Rebuild();
            Changed?.Invoke();
        }

        private void Rebuild()
        {
            _roots.Clear();
            var character = _view.Character;
            if (character != null)
            {
                _roots.Add(new InventoryRoot(InventoryRootKind.Character, character.Root));
            }

            foreach (var id in _openedOrder)
            {
                if (_view.Containers.TryGetValue(id, out var container) && container != character)
                {
                    _roots.Add(new InventoryRoot(KindOf(container.Kind), container.Root));
                }
            }
        }

        private static InventoryRootKind KindOf(RootKind kind)
        {
            switch (kind)
            {
                case RootKind.Character:
                    return InventoryRootKind.Character;
                case RootKind.Body:
                    return InventoryRootKind.Body;
                case RootKind.Ground:
                    return InventoryRootKind.Ground;
                default:
                    return InventoryRootKind.Container;
            }
        }
    }
}
