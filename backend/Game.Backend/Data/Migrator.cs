using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace RaidZero.Backend.Data;

/// <summary>
/// Applies the numbered SQL files in Migrations/ (embedded in the assembly) in order, each in its own
/// transaction, and records them in schema_migrations.
///
/// It refuses to start when the database is ahead of the code, or when a migration that already ran
/// has since been edited. Both usually mean the wrong build is pointed at the wrong database.
/// </summary>
public static class Migrator
{
    private const string ResourcePrefix = "migrations/";

    public static IReadOnlyList<Migration> LoadEmbedded() => Load(
        typeof(Migrator).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => (n[ResourcePrefix.Length..], ReadResource(n))));

    /// <summary>Parses "0001_initial.sql"-style names and checks the versions run 1, 2, 3... with no gaps.</summary>
    public static IReadOnlyList<Migration> Load(IEnumerable<(string FileName, string Sql)> files)
    {
        var migrations = new List<Migration>();
        foreach (var (fileName, sql) in files)
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var underscore = stem.IndexOf('_');
            if (underscore <= 0 || !int.TryParse(stem[..underscore], out var version) || version <= 0)
            {
                throw new InvalidOperationException($"Migration '{fileName}' must be named like 0001_description.sql.");
            }

            migrations.Add(new Migration(version, stem[(underscore + 1)..], sql, Checksum(sql)));
        }

        migrations.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
            {
                throw new InvalidOperationException($"Migrations must be numbered 1, 2, 3... without gaps; found {migrations[i].Version} at position {i + 1}.");
            }
        }

        return migrations;
    }

    /// <summary>Brings the database up to date and returns its schema version.</summary>
    public static async Task<int> MigrateAsync(Database database, IReadOnlyList<Migration> migrations, ILogger? logger = null, CancellationToken ct = default)
    {
        var applied = await database.WriteAsync(async (connection, tx) =>
        {
            await connection.ExecuteAsync(
                """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version    INTEGER PRIMARY KEY,
                    name       TEXT NOT NULL,
                    checksum   TEXT NOT NULL,
                    applied_at TIMESTAMP NOT NULL
                );
                """, transaction: tx);

            var rows = await connection.QueryAsync<AppliedMigration>(
                "SELECT version AS Version, name AS Name, checksum AS Checksum FROM schema_migrations ORDER BY version",
                transaction: tx);
            return rows.ToList();
        }, ct);

        foreach (var row in applied)
        {
            var known = migrations.FirstOrDefault(m => m.Version == row.Version);
            if (known is null)
            {
                throw new InvalidOperationException(
                    $"The database has migration {row.Version} ({row.Name}), which this build doesn't know. Is an older build pointed at a newer database?");
            }

            if (!string.Equals(known.Checksum, row.Checksum, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Migration {row.Version} ({row.Name}) was edited after it was applied. Revert the edit and add a new migration instead.");
            }
        }

        var appliedVersions = applied.Select(r => (int)r.Version).ToHashSet();
        foreach (var migration in migrations.Where(m => !appliedVersions.Contains(m.Version)))
        {
            var ran = await database.WriteAsync(async (connection, tx) =>
            {
                // Re-check under the write lock in case another process applied it since the read above.
                var already = await connection.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM schema_migrations WHERE version = @Version", new { migration.Version }, transaction: tx);
                if (already > 0)
                {
                    return false;
                }

                await connection.ExecuteAsync(migration.Sql, transaction: tx);
                await connection.ExecuteAsync(
                    "INSERT INTO schema_migrations (version, name, checksum, applied_at) VALUES (@Version, @Name, @Checksum, @AppliedAt)",
                    new { migration.Version, migration.Name, migration.Checksum, AppliedAt = DateTime.UtcNow.ToString("O") },
                    transaction: tx);
                return true;
            }, ct);

            if (ran)
            {
                logger?.LogInformation("Applied migration {Version} ({Name})", migration.Version, migration.Name);
            }
        }

        return migrations.Count == 0 ? 0 : migrations[^1].Version;
    }

    // Line endings are normalized first so a Windows checkout (core.autocrlf) has the same checksum.
    internal static string Checksum(string sql)
    {
        var normalized = sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(Migrator).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded migration {name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class AppliedMigration
    {
        public long Version { get; set; }

        public string Name { get; set; } = "";

        public string Checksum { get; set; } = "";
    }
}

public sealed record Migration(int Version, string Name, string Sql, string Checksum);
