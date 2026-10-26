using System.Collections.Generic;

namespace RaidZero.Shared.Inventory
{
    /// <summary>
    /// One item instance and everything inside it. This is the wire shape used by the backend's
    /// join and outcome payloads and by the game server's inventory views:
    /// { id, def_id, slot, x, y, rotated, quantity, durability, children }.
    /// </summary>
    public sealed class ItemNode
    {
        /// <summary>UUIDv7. Minted by the backend, or by the game server for raid loot.</summary>
        public string Id { get; set; }

        public string DefId { get; set; }

        /// <summary>
        /// Where this sits in its parent: "grid" for grid placement, or a named slot such as "primary".
        /// Null only on roots (the stash and character containers).
        /// </summary>
        public string Slot { get; set; }

        /// <summary>Grid column. Set only when Slot is "grid".</summary>
        public int? X { get; set; }

        /// <summary>Grid row. Set only when Slot is "grid".</summary>
        public int? Y { get; set; }

        /// <summary>Swaps width and height for grid placement. Always false in named slots.</summary>
        public bool Rotated { get; set; }

        public int Quantity { get; set; } = 1;

        /// <summary>0..1 for items whose definition has durability; null otherwise.</summary>
        public double? Durability { get; set; }

        public List<ItemNode> Children { get; set; } = new List<ItemNode>();

        /// <summary>This node and every descendant, depth first.</summary>
        public IEnumerable<ItemNode> Walk()
        {
            var stack = new Stack<ItemNode>();
            stack.Push(this);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                yield return node;
                if (node.Children == null)
                {
                    continue;
                }

                for (int i = node.Children.Count - 1; i >= 0; i--)
                {
                    if (node.Children[i] != null)
                    {
                        stack.Push(node.Children[i]);
                    }
                }
            }
        }

        /// <summary>A deep copy, so callers can try a change without touching the original tree.</summary>
        public ItemNode Clone()
        {
            var copy = new ItemNode
            {
                Id = Id,
                DefId = DefId,
                Slot = Slot,
                X = X,
                Y = Y,
                Rotated = Rotated,
                Quantity = Quantity,
                Durability = Durability,
                Children = new List<ItemNode>(),
            };

            if (Children != null)
            {
                foreach (var child in Children)
                {
                    copy.Children.Add(child?.Clone());
                }
            }

            return copy;
        }
    }

    /// <summary>
    /// Slot names used across Unity and the backend. Change these only in a pull request that
    /// touches both sides; see "The contract" in the Unity plan.
    /// </summary>
    public static class SlotNames
    {
        public const string Grid = "grid";
        public const string Primary = "primary";
        public const string Secondary = "secondary";
        public const string Armor = "armor";
        public const string Rig = "rig";
        public const string Backpack = "backpack";
        public const string Magazine = "magazine";
        public const string Rounds = "rounds";
    }
}
