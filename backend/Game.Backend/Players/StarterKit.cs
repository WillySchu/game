using System.Text.Json;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;

namespace RaidZero.Backend.Players;

/// <summary>
/// The gear every new player starts with, read from starter_kit.json. Entries are item specs (see
/// <see cref="ItemSpecs"/>): the item tree shape without IDs; grid items may leave out x and y.
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
        try
        {
            ItemSpecs.AddChildren(_catalog, stash, _document.Stash, newId);
            ItemSpecs.AddChildren(_catalog, character, _document.Character, newId);
        }
        catch (ItemSpecException ex)
        {
            throw new StarterKitException(ex.Message, ex);
        }

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
