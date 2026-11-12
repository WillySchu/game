using System;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;

namespace RaidZero.Shared.Trading
{
    /// <summary>
    /// What a trader pays for things: a share (the trader's "buys at", such as 0.5) of each item's
    /// <see cref="ItemDefinition.Value"/>, times the quantity, and less for worn gear. Everything inside counts
    /// too, so a rifle sells with its magazine and the rounds in it. The backend and the Hideout both use this,
    /// so the price shown is the price paid.
    /// </summary>
    public static class TraderPrices
    {
        /// <summary>What a fully worn item still fetches, as a share of a new one's price.</summary>
        public const double WornShare = 0.25;

        /// <summary>1 for a new item, down to <see cref="WornShare"/> at no durability. 1 for items without durability.</summary>
        public static double Condition(ItemDefinition definition, ItemNode item)
        {
            if (definition == null || !definition.HasDurability || item?.Durability == null)
            {
                return 1.0;
            }

            double durability = Math.Max(0.0, Math.Min(1.0, item.Durability.Value));
            return WornShare + (1.0 - WornShare) * durability;
        }

        /// <summary>What the trader pays for the item and everything in it, in whole credits (rounded down).</summary>
        public static long SellPrice(ItemCatalog catalog, ItemNode item, double buysAt)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            double total = 0;
            foreach (var node in item.Walk())
            {
                if (catalog.TryGet(node.DefId, out var definition))
                {
                    total += definition.Value * (double)Math.Max(1, node.Quantity) * Condition(definition, node) * buysAt;
                }
            }

            // A hair of tolerance, so 0.5 * 30 rounds of 120 credits isn't 1799 from floating point.
            return (long)Math.Floor(total + 1e-6);
        }
    }
}
