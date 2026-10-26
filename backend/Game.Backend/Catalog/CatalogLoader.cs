using System.Text.Json;
using RaidZero.Shared.Catalog;

namespace RaidZero.Backend.Catalog;

/// <summary>
/// Loads item_catalog.json at startup and refuses to continue if anything is off. A backend running
/// on a bad or stale catalog could commit items it doesn't understand, so failing fast is the point.
/// </summary>
public static class CatalogLoader
{
    public static ItemCatalog LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new CatalogLoadException($"Item catalog not found at '{Path.GetFullPath(path)}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    /// <param name="json">The catalog file's contents.</param>
    /// <param name="source">Where it came from, for error messages.</param>
    public static ItemCatalog Parse(string json, string source = "item_catalog.json")
    {
        CatalogDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<CatalogDocument>(json, JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            throw new CatalogLoadException($"{source} is not valid JSON: {ex.Message}", ex);
        }

        if (document?.Items is null || document.Items.Count == 0)
        {
            throw new CatalogLoadException($"{source} has no items.");
        }

        ItemCatalog catalog;
        try
        {
            catalog = new ItemCatalog(document.Items);
        }
        catch (CatalogException ex)
        {
            throw new CatalogLoadException($"{source}: {ex.Message}", ex);
        }

        RequireRoot(catalog, WellKnownDefs.Stash, needsGrid: true, source);
        RequireRoot(catalog, WellKnownDefs.Character, needsGrid: false, source);

        if (!string.Equals(document.CatalogVersion, catalog.Version, StringComparison.Ordinal))
        {
            throw new CatalogLoadException(
                $"{source} declares catalog_version '{document.CatalogVersion}' but its contents hash to '{catalog.Version}'. " +
                "Re-export the catalog from Unity, or if you edited it by hand, set catalog_version to the computed value.");
        }

        return catalog;
    }

    private static void RequireRoot(ItemCatalog catalog, string defId, bool needsGrid, string source)
    {
        if (!catalog.TryGet(defId, out var def))
        {
            throw new CatalogLoadException($"{source} must define the '{defId}' root container.");
        }

        if (needsGrid && def.Grid is null)
        {
            throw new CatalogLoadException($"{source}: '{defId}' must have a grid.");
        }

        if (!needsGrid && def.Slots.Count == 0)
        {
            throw new CatalogLoadException($"{source}: '{defId}' must have equipment slots.");
        }
    }
}

public sealed class CatalogLoadException : Exception
{
    public CatalogLoadException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
