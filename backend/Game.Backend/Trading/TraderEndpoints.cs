using System.Security.Claims;
using RaidZero.Backend.Auth;
using RaidZero.Backend.Players;
using RaidZero.Shared.Inventory;

namespace RaidZero.Backend.Trading;

/// <summary>GET /v1/me/traders, and buying from and selling to a trader.</summary>
public static class TraderEndpoints
{
    /// <summary>The most items one sale can list.</summary>
    public const int MaxItemsPerSale = 50;

    public sealed record TradersResponse(IReadOnlyList<TraderResponse> Traders);

    public sealed record TraderResponse(string TraderId, string DisplayName, double BuysAt, IReadOnlyList<OfferResponse> Offers);

    /// <param name="Item">What one purchase gives, as an item tree without IDs.</param>
    /// <param name="Stock">How many per restock; null for no limit.</param>
    /// <param name="Left">How many this player can still buy before it restocks; null for no limit.</param>
    public sealed record OfferResponse(string OfferId, long Price, ItemNode Item, int? Stock, int? Left, DateTimeOffset? RestocksAt);

    /// <param name="ExpectedVersion">The profile_version the client's view is based on. Required.</param>
    /// <param name="Count">How many to buy; 1 if left out.</param>
    public sealed record BuyRequest(long? ExpectedVersion, string? OfferId, int? Count);

    public sealed record SellRequest(long? ExpectedVersion, List<string>? ItemIds);

    /// <param name="Created">The items bought, at the top of the stash.</param>
    public sealed record BuyResponse(long ProfileVersion, long Price, IReadOnlyList<string> Created);

    /// <param name="Sold">The items sold, and everything that was in them.</param>
    public sealed record SellResponse(long ProfileVersion, long Credits, IReadOnlyList<string> Sold);

    /// <param name="Reason">A TradeErrorCodes value, such as "not_enough_money".</param>
    public sealed record InvalidTradeError(string Error, string Reason, string Message, string? ItemId);

    public static void MapTraderEndpoints(this WebApplication app)
    {
        var traders = app.MapGroup("/v1/me/traders").RequireAuthorization(Policies.Player);
        traders.MapGet("", ListAsync);
        traders.MapPost("/{traderId}/buy", BuyAsync);
        traders.MapPost("/{traderId}/sell", SellAsync);
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, TraderService service, CancellationToken ct)
    {
        var list = new List<TraderResponse>();
        foreach (var trader in service.Traders)
        {
            var offers = await service.OffersAsync(user.GetPlayerId(), trader, ct);
            list.Add(new TraderResponse(
                trader.TraderId,
                trader.DisplayName,
                trader.BuysAt,
                offers.Select(o => new OfferResponse(o.Offer.OfferId, o.Offer.Price, o.Offer.Item, o.Offer.Stock, o.Left, o.RestocksAt)).ToList()));
        }

        return Results.Ok(new TradersResponse(list));
    }

    private static async Task<IResult> BuyAsync(string traderId, BuyRequest body, ClaimsPrincipal user, TraderService service, CancellationToken ct)
    {
        if (body.ExpectedVersion is null)
        {
            return Results.UnprocessableEntity(new ApiError("invalid_request", "expected_version is required."));
        }

        if (string.IsNullOrEmpty(body.OfferId))
        {
            return Results.UnprocessableEntity(new ApiError("invalid_request", "offer_id is required."));
        }

        var result = await service.BuyAsync(user.GetPlayerId(), body.ExpectedVersion.Value, traderId, body.OfferId, body.Count ?? 1, ct);
        return Answer(result, () => Results.Ok(new BuyResponse(result.ProfileVersion, result.Credits, result.ItemIds)));
    }

    private static async Task<IResult> SellAsync(string traderId, SellRequest body, ClaimsPrincipal user, TraderService service, CancellationToken ct)
    {
        if (body.ExpectedVersion is null)
        {
            return Results.UnprocessableEntity(new ApiError("invalid_request", "expected_version is required."));
        }

        if (body.ItemIds is null || body.ItemIds.Count == 0)
        {
            return Results.UnprocessableEntity(new ApiError("invalid_request", "item_ids must list at least one item."));
        }

        if (body.ItemIds.Count > MaxItemsPerSale)
        {
            return Results.UnprocessableEntity(new ApiError("invalid_request", $"At most {MaxItemsPerSale} items per sale."));
        }

        var result = await service.SellAsync(user.GetPlayerId(), body.ExpectedVersion.Value, traderId, body.ItemIds, ct);
        return Answer(result, () => Results.Ok(new SellResponse(result.ProfileVersion, result.Credits, result.ItemIds)));
    }

    private static IResult Answer(TradeResult result, Func<IResult> done) => result.Status switch
    {
        TradeStatus.Done => done(),
        TradeStatus.UnknownTrader => Results.NotFound(new ApiError("unknown_trader", "There's no trader by that name.")),
        TradeStatus.VersionConflict => Results.Conflict(new PlayerEndpoints.VersionConflictError("version_conflict", result.ProfileVersion)),
        TradeStatus.InRaid => Results.Conflict(new PlayerEndpoints.InRaidError("in_raid", result.RaidId!, "Your gear is locked while you're in a raid.")),
        TradeStatus.Rejected => Results.UnprocessableEntity(new InvalidTradeError("invalid_trade", result.Error!, result.Message!, result.ItemId)),
        _ => Results.NotFound(new ApiError("player_not_found", "The player in this token no longer exists.")),
    };
}
