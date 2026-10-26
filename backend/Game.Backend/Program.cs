using Dapper;
using Microsoft.Extensions.Options;
using RaidZero.Backend;
using RaidZero.Backend.Catalog;
using RaidZero.Backend.Data;
using RaidZero.Shared.Catalog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<BackendOptions>(builder.Configuration.GetSection(BackendOptions.SectionName));
builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Configure(o.SerializerOptions));

string ResolvePath(string path) => Path.GetFullPath(path, builder.Environment.ContentRootPath);

builder.Services.AddSingleton(sp => new Database(ResolvePath(sp.GetRequiredService<IOptions<BackendOptions>>().Value.DatabasePath)));
builder.Services.AddSingleton<ItemCatalog>(sp => CatalogLoader.LoadFile(ResolvePath(sp.GetRequiredService<IOptions<BackendOptions>>().Value.CatalogPath)));

var app = builder.Build();

// Fail fast: a backend with a bad catalog or an unexpected schema must not accept traffic.
var catalog = app.Services.GetRequiredService<ItemCatalog>();
var database = app.Services.GetRequiredService<Database>();
await database.InitializeAsync();
var schemaVersion = await Migrator.MigrateAsync(database, Migrator.LoadEmbedded(), app.Logger);
app.Logger.LogInformation(
    "Ready: {ItemCount} item definitions (catalog {CatalogVersion}), schema version {SchemaVersion}, database {DatabasePath}",
    catalog.Count, catalog.Version, schemaVersion, database.FilePath);

app.MapGet("/healthz", async (Database db, ItemCatalog items, CancellationToken ct) =>
{
    var schema = await db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT COALESCE(MAX(version), 0) FROM schema_migrations"), ct);
    return Results.Ok(new { Status = "ok", CatalogVersion = items.Version, SchemaVersion = schema });
});

// .NET 10 makes the generated Program class public, so WebApplicationFactory<Program> works in tests
// without a "public partial class Program" declaration here.
app.Run();
