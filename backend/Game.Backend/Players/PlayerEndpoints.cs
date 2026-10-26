using System.Security.Claims;
using RaidZero.Backend.Auth;
using RaidZero.Shared.Inventory;

namespace RaidZero.Backend.Players;

public static class PlayerEndpoints
{
    public sealed record DevLoginRequest(string? DisplayName);

    public sealed record DevLoginResponse(string PlayerId, string DisplayName, bool Created, string Token, DateTimeOffset ExpiresAt);

    /// <param name="ExpectedVersion">The profile_version the client's view is based on. Required.</param>
    public sealed record InventoryOpsRequest(long? ExpectedVersion, List<InventoryOp>? Ops);

    public sealed record InventoryOpsResponse(long ProfileVersion, IReadOnlyList<CreatedItem> Created);

    public sealed record VersionConflictError(string Error, long ProfileVersion);

    /// <param name="OpIndex">The rejected op; null when the batch as a whole failed validation.</param>
    /// <param name="Reason">An InventoryErrorCodes value, such as "slot_occupied".</param>
    public sealed record InvalidOpError(string Error, int? OpIndex, string Reason, string Message);

    public static void MapPlayerEndpoints(this WebApplication app, BackendOptions options)
    {
        if (options.DevLoginEnabled)
        {
            app.MapPost("/v1/auth/dev-login", DevLoginAsync);
        }

        var me = app.MapGroup("/v1/me").RequireAuthorization();
        me.MapGet("/profile", GetProfileAsync);
        me.MapPost("/inventory/ops", ApplyInventoryOpsAsync);
    }

    private static async Task<IResult> DevLoginAsync(DevLoginRequest body, PlayerStore players, PlayerTokens tokens, CancellationToken ct)
    {
        if (!DisplayNames.IsValid(body.DisplayName))
        {
            return Results.UnprocessableEntity(new ApiError(
                "invalid_display_name",
                $"Use {DisplayNames.MinLength} to {DisplayNames.MaxLength} letters, digits, underscores or hyphens."));
        }

        var login = await players.LoginOrCreateAsync(body.DisplayName!, ct);
        var token = tokens.Issue(login.PlayerId);
        return Results.Ok(new DevLoginResponse(login.PlayerId, login.DisplayName, login.Created, token.Token, token.ExpiresAt));
    }

    private static async Task<IResult> GetProfileAsync(ClaimsPrincipal user, PlayerStore players, CancellationToken ct)
    {
        var profile = await players.GetProfileAsync(user.GetPlayerId(), ct);
        return profile is null
            ? Results.NotFound(new ApiError("player_not_found", "The player in this token no longer exists."))
            : Results.Ok(profile);
    }

    private static async Task<IResult> ApplyInventoryOpsAsync(InventoryOpsRequest body, ClaimsPrincipal user, StashInventory stash, CancellationToken ct)
    {
        if (body.ExpectedVersion is null)
        {
            return Results.UnprocessableEntity(new ApiError("invalid_request", "expected_version is required."));
        }

        if (body.Ops is null || body.Ops.Count == 0)
        {
            return Results.UnprocessableEntity(new ApiError("invalid_request", "ops must list at least one operation."));
        }

        if (body.Ops.Count > StashInventory.MaxOpsPerRequest)
        {
            return Results.UnprocessableEntity(new ApiError("invalid_request", $"At most {StashInventory.MaxOpsPerRequest} ops per request."));
        }

        var result = await stash.ApplyAsync(user.GetPlayerId(), body.ExpectedVersion.Value, body.Ops, ct);
        return result.Status switch
        {
            StashOpsStatus.Applied => Results.Ok(new InventoryOpsResponse(result.ProfileVersion, result.Created)),
            StashOpsStatus.VersionConflict => Results.Conflict(new VersionConflictError("version_conflict", result.ProfileVersion)),
            StashOpsStatus.Rejected => Results.UnprocessableEntity(new InvalidOpError("invalid_op", result.FailedOpIndex, result.Error!.Code, result.Error.Message)),
            _ => Results.NotFound(new ApiError("player_not_found", "The player in this token no longer exists.")),
        };
    }
}
