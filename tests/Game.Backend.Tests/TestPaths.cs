using Microsoft.Data.Sqlite;
using RaidZero.Backend.Data;

namespace Game.Backend.Tests;

internal static class TestPaths
{
    /// <summary>The repo root, found by walking up to RaidZero.sln.</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string CatalogPath => Path.Combine(RepoRoot, "shared", "catalog", "item_catalog.json");

    public static string StarterKitPath => Path.Combine(RepoRoot, "backend", "Game.Backend", "starter_kit.json");

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "RaidZero.sln")))
                {
                    return dir.FullName;
                }
            }
        }

        throw new InvalidOperationException("Could not find RaidZero.sln above the test output folder.");
    }
}

/// <summary>A fresh database file per test, deleted afterwards.</summary>
public sealed class TempDatabase : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "raidzero-tests", Guid.NewGuid().ToString("N"));

    public TempDatabase()
    {
        Database = new Database(Path.Combine(_folder, "test.db"));
    }

    public Database Database { get; }

    public void Dispose()
    {
        // Pooled connections keep the file open, which blocks deleting it on Windows.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS cleans the temp folder eventually.
        }
    }
}

/// <summary>A clock tests can move.</summary>
public sealed class FixedTime : TimeProvider
{
    public FixedTime(DateTimeOffset now) => Now = now;

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}
