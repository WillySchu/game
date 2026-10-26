namespace RaidZero.Backend;

/// <summary>The error body every endpoint uses: { "error": "invalid_display_name", "message": "..." }.</summary>
public sealed record ApiError(string Error, string? Message = null);
