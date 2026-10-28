using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RaidZero.Shared.Catalog
{
    /// <summary>
    /// Computes the catalog version: a hash over every field that affects inventory rules.
    ///
    /// The Unity exporter writes it into item_catalog.json, the game server sends it on every backend
    /// call, and the backend rejects mismatches. Cosmetic fields (display names) are left out, so a
    /// rename never forces a redeploy. Definition, tag and slot order don't matter either; grid order does,
    /// since it decides which grid "the first free spot" fills first.
    /// </summary>
    public static class CatalogHash
    {
        public static string Compute(IEnumerable<ItemDefinition> definitions)
        {
            if (definitions == null)
            {
                throw new ArgumentNullException(nameof(definitions));
            }

            var text = new StringBuilder();
            foreach (var def in definitions.OrderBy(d => d.DefId, StringComparer.Ordinal))
            {
                AppendString(text, def.DefId);
                AppendInt(text, def.Width);
                AppendInt(text, def.Height);
                AppendInt(text, def.MaxStack);
                text.Append(def.HasDurability ? "D1" : "D0");
                AppendList(text, def.Tags);

                var grids = def.Grids ?? new List<GridSpec>();
                text.Append('G');
                AppendInt(text, grids.Count);
                foreach (var grid in grids)
                {
                    AppendString(text, grid?.Name);
                    AppendInt(text, grid?.Width ?? 0);
                    AppendInt(text, grid?.Height ?? 0);
                }

                var slots = (def.Slots ?? new List<SlotSpec>()).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
                AppendInt(text, slots.Count);
                foreach (var slot in slots)
                {
                    AppendString(text, slot.Name);
                    AppendList(text, slot.Accepts);
                    AppendInt(text, slot.MaxQuantity);
                }

                text.Append('\n');
            }

            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
                var hex = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                {
                    hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return hex.ToString();
            }
        }

        // Length-prefixed so no value can be confused with a separator.
        private static void AppendString(StringBuilder text, string value)
        {
            value = value ?? string.Empty;
            text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
        }

        private static void AppendInt(StringBuilder text, int value)
        {
            text.Append('#').Append(value.ToString(CultureInfo.InvariantCulture)).Append(';');
        }

        private static void AppendList(StringBuilder text, List<string> values)
        {
            var sorted = (values ?? new List<string>()).OrderBy(v => v, StringComparer.Ordinal).ToList();
            AppendInt(text, sorted.Count);
            foreach (var value in sorted)
            {
                AppendString(text, value);
            }
        }
    }
}
