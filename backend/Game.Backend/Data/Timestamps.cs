using System.Globalization;

namespace RaidZero.Backend.Data;

/// <summary>
/// The one timestamp format the database uses: round-trip ISO-8601 in UTC, a fixed 28 characters
/// ("2026-10-26T20:00:00.0000000Z"). Fixed width means SQL can compare timestamps as plain strings,
/// which the ticket-expiry and lease checks rely on.
/// </summary>
public static class Timestamps
{
    public static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
