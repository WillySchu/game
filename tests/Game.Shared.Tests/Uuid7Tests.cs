using System.Security.Cryptography;
using RaidZero.Shared.Ids;

namespace Game.Shared.Tests;

public class Uuid7Tests
{
    [Fact]
    public void NewId_IsCanonicalVersion7WithRfcVariant()
    {
        string id = Uuid7.NewId();

        Assert.True(Uuid7.IsValid(id));
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", id);
    }

    [Fact]
    public void Next_IsStrictlyIncreasingWithinOneMillisecond()
    {
        var generator = new Uuid7(() => 1_700_000_000_000, RandomNumberGenerator.Create());

        string previous = generator.Next();
        for (int i = 0; i < 10_000; i++)
        {
            string current = generator.Next();
            Assert.True(string.CompareOrdinal(previous, current) < 0, $"{current} should sort after {previous}");
            previous = current;
        }
    }

    [Fact]
    public void Next_StaysIncreasingWhenTheClockGoesBackwards()
    {
        long now = 1_700_000_000_500;
        var generator = new Uuid7(() => now, RandomNumberGenerator.Create());

        string before = generator.Next();
        now -= 400;
        string after = generator.Next();

        Assert.True(string.CompareOrdinal(before, after) < 0);
    }

    [Fact]
    public void TryGetTimestamp_ReturnsTheGeneratorsClock()
    {
        var generator = new Uuid7(() => 1_700_000_123_456, RandomNumberGenerator.Create());

        Assert.True(Uuid7.TryGetTimestamp(generator.Next(), out var timestamp));
        Assert.Equal(1_700_000_123_456, timestamp.ToUnixTimeMilliseconds());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("0192A1B2-C3D4-7E5F-8A6B-7C8D9E0F1A2B")]
    [InlineData("0192a1b2c3d47e5f8a6b7c8d9e0f1a2b")]
    [InlineData("0192a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2")]
    public void IsValid_RejectsAnythingButLowercaseCanonical(string? value)
    {
        Assert.False(Uuid7.IsValid(value!));
    }

    [Fact]
    public void TryGetTimestamp_RejectsOtherVersions()
    {
        Assert.False(Uuid7.TryGetTimestamp("0192a1b2-c3d4-4e5f-8a6b-7c8d9e0f1a2b", out _));
    }
}
