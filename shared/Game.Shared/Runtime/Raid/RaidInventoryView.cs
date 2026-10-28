using System;
using System.Collections.Generic;
using RaidZero.Shared.Inventory;

namespace RaidZero.Shared.Raid
{
    /// <summary>
    /// The client's side of the raid inventory: what the player can see, and which of their requests are
    /// still waiting for the server. The inventory screen draws from <see cref="Containers"/>, sends the
    /// bytes the request methods return, and hands everything the server sends to <see cref="Receive"/>.
    ///
    /// Nothing changes locally when a request is sent. Grey out the items <see cref="IsPending"/> reports
    /// until the answer comes; the server's views then replace the containers they cover. The client's own
    /// fit checks (InventoryTree, GridPlacement) can still show where an item would go while dragging.
    ///
    /// Not thread-safe; use it from the main thread.
    /// </summary>
    public sealed class RaidInventoryView
    {
        private readonly Dictionary<string, ContainerView> _containers = new Dictionary<string, ContainerView>(StringComparer.Ordinal);
        private readonly Dictionary<uint, List<string>> _pending = new Dictionary<uint, List<string>>();
        private uint _nextRequestId = 1;

        /// <summary>A container's contents changed, or the player can now see it.</summary>
        public event Action<ContainerView> ContainerChanged;

        /// <summary>The player can't see this container any more. Close its window.</summary>
        public event Action<string> ContainerClosed;

        /// <summary>A request was answered. On failure, Error says why (show it, and un-grey the items).</summary>
        public event Action<RequestResult> RequestCompleted;

        /// <summary>Every container the player can see, by ID: their own gear and whatever they have open.</summary>
        public IReadOnlyDictionary<string, ContainerView> Containers => _containers;

        /// <summary>The player's own gear, once the server has sent it.</summary>
        public ContainerView Character
        {
            get
            {
                foreach (var view in _containers.Values)
                {
                    if (view.Kind == RootKind.Character)
                    {
                        return view;
                    }
                }

                return null;
            }
        }

        public int PendingCount => _pending.Count;

        /// <summary>Whether an item is part of a request still waiting for an answer.</summary>
        public bool IsPending(string itemId)
        {
            foreach (var ids in _pending.Values)
            {
                if (ids.Contains(itemId))
                {
                    return true;
                }
            }

            return false;
        }

        public byte[] Request(IReadOnlyList<InventoryOp> ops)
        {
            var request = new InventoryRequest { RequestId = NextId(), Ops = new List<InventoryOp>(ops) };
            var touched = new List<string>();
            foreach (var op in ops)
            {
                touched.Add(op.ItemId);
                if (op.IntoId != null)
                {
                    touched.Add(op.IntoId);
                }
            }

            return Send(request, touched);
        }

        public byte[] PickUp(string itemId) => Send(new PickUpRequest { RequestId = NextId(), ItemId = itemId }, new List<string> { itemId });

        public byte[] Drop(string itemId) => Send(new DropRequest { RequestId = NextId(), ItemId = itemId }, new List<string> { itemId });

        public byte[] Open(string containerId) => Send(new OpenRequest { RequestId = NextId(), ContainerId = containerId }, new List<string>());

        /// <summary>Closes the window right away; the server needs no answer.</summary>
        public byte[] Close(string containerId)
        {
            if (_containers.Remove(containerId))
            {
                ContainerClosed?.Invoke(containerId);
            }

            return RaidProtocol.Encode(new CloseRequest { ContainerId = containerId });
        }

        /// <summary>Applies one message from the server. Throws <see cref="ProtocolException"/> if it's malformed.</summary>
        public void Receive(byte[] data)
        {
            switch (RaidProtocol.DecodeFromServer(data))
            {
                case ContainerView view:
                    _containers[view.Root.Id] = view;
                    ContainerChanged?.Invoke(view);
                    break;
                case ContainerClosed closed:
                    if (_containers.Remove(closed.ContainerId))
                    {
                        ContainerClosed?.Invoke(closed.ContainerId);
                    }

                    break;
                case RequestResult result:
                    _pending.Remove(result.RequestId);
                    RequestCompleted?.Invoke(result);
                    break;
            }
        }

        /// <summary>Forgets everything, such as after dying or leaving the raid.</summary>
        public void Clear()
        {
            _containers.Clear();
            _pending.Clear();
        }

        private uint NextId()
        {
            var id = _nextRequestId++;
            if (_nextRequestId == 0)
            {
                _nextRequestId = 1;
            }

            return id;
        }

        private byte[] Send(RaidMessage message, List<string> touched)
        {
            var bytes = RaidProtocol.Encode(message);
            _pending[((RaidRequest)message).RequestId] = touched;
            return bytes;
        }
    }
}
