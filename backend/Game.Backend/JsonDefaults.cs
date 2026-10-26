using System.Text.Json;

namespace RaidZero.Backend;

/// <summary>
/// One set of JSON settings for the HTTP API, the catalog file and stored JSON columns.
/// snake_case matches the API spec and lets the shared classes stay free of serializer attributes.
/// </summary>
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions());

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.ReadCommentHandling = JsonCommentHandling.Skip;
        options.AllowTrailingCommas = true;

        // Leaves out x, y and durability where they don't apply, which keeps item trees small.
        options.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        return options;
    }
}
