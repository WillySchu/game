using System.Text.Json;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;

namespace RaidZero.Backend.Players;

/// <summary>
/// The gear every new player starts with, read from starter_kit.json. Entries use the item tree shape
/// without IDs; grid items may leave out x and y to take the first free spot.
///
/// The kit is checked against the catalog at startup: if an item rule changes and the kit no longer
/// fits, the backend refuses to start rather than handing out gear that fails validation later.
/// </summary>
public sealed class StarterKit
{
    private readonly ItemCatalog _catalog;
    private readonly Document _document;

    private StarterKit(ItemCatalog catalog, Document document)
    {
        _catalog = catalog;
        _document = document;
    }

    public static StarterKit LoadFile(string path, ItemCatalog catalog)
    {
        if (!File.Exists(path))
        {
            throw new StarterKitException($"Starter kit not found at '{Path.GetFullPath(path)}'.");
        }

        return Parse(File.ReadAllText(path), catalog, path);
    }

    public static StarterKit Parse(string json, ItemCatalog catalog, string source = "starter_kit.json")
    {
        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(json, JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            throw new StarterKitException($"{source} is not valid JSON: {ex.Message}", ex);
        }

        var kit = new StarterKit(catalog, document ?? new Document());

        // Build it once now, so every problem surfaces at startup instead of at a player's first login.
        (ItemNode stash, ItemNode character) trees;
        try
        {
            trees = kit.Instantiate();
        }
        catch (StarterKitException ex)
        {
            throw new StarterKitException($"{source}: {ex.Message}", ex);
        }

        var errors = InventoryValidator.Validate(catalog, trees.stash)
            .Concat(InventoryValidator.Validate(catalog, trees.character))
            .Select(e => $"{e.Code}: {e.Message}")
            .ToList();
        if (errors.Count > 0)
        {
            throw new StarterKitException($"{source} doesn't fit the item catalog:\n  " + string.Join("\n  ", errors));
        }

        return kit;
    }

    /// <summary>Builds a fresh stash and character with new IDs.</summary>
    public (ItemNode Stash, ItemNode Character) Instantiate(Func<string>? newId = null)
    {
        newId ??= Uuid7.NewId;
        var stash = new ItemNode { Id = newId(), DefId = WellKnownDefs.Stash };
        var character = new ItemNode { Id = newId(), DefId = WellKnownDefs.Character };
        AddChildren(stash, _document.Stash, newId);
        AddChildren(character, _document.Character, newId);
        return (stash, character);
    }

    /// <summary>How many items, counting the two roots, each new player receives.</summary>
    public int ItemCount
    {
        get
        {
            var (stash, character) = Instantiate();
            return stash.Walk().Count() + character.Walk().Count();
        }
    }

    private void AddChildren(ItemNode parent, List<ItemNode>? specs, Func<string> newId)
    {
        foreach (var spec in specs ?? [])
        {
            if (!_catalog.TryGet(spec.DefId, out var def))
            {
                throw new StarterKitException($"unknown def_id '{spec.DefId}'.");
            }

            var child = new ItemNode
            {
                Id = newId(),
                DefId = spec.DefId,
                Slot = spec.Slot,
                X = spec.X,
                Y = spec.Y,
                Rotated = spec.Rotated,
                Quantity = spec.Quantity,
                Durability = spec.Durability,
            };

            if (child.Slot == SlotNames.Grid && child.X is null && child.Y is null)
            {
                if (!GridPlacement.TryFindFreeSpot(_catalog, parent, def, out var x, out var y, out var rotated))
                {
                    throw new StarterKitException($"no room for {spec.DefId} in {parent.DefId}.");
                }

                child.X = x;
                child.Y = y;
                child.Rotated = rotated;
            }

            parent.Children.Add(child);
            AddChildren(child, spec.Children, newId);
        }
    }

    private sealed class Document
    {
        public List<ItemNode> Stash { get; set; } = [];

        public List<ItemNode> Character { get; set; } = [];
    }
}

public sealed class StarterKitException : Exception
{
    public StarterKitException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
