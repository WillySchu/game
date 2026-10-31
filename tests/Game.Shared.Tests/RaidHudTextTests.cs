using RaidZero.Shared.Raid;

namespace Game.Shared.Tests;

public class RaidHudTextTests
{
    [Theory]
    [InlineData(1500, "25:00")]
    [InlineData(1499.2, "25:00")]
    [InlineData(61, "1:01")]
    [InlineData(59.01, "1:00")]
    [InlineData(0.4, "0:01")]
    [InlineData(0, "0:00")]
    [InlineData(-3, "0:00")]
    [InlineData(3900, "1:05:00")]
    public void Clock_RoundsUpToWholeSeconds(double seconds, string expected) => Assert.Equal(expected, RaidHudText.Clock(seconds));

    [Theory]
    [InlineData(8, 8, 0f)]
    [InlineData(4, 8, 0.5f)]
    [InlineData(0, 8, 1f)]
    [InlineData(10, 8, 0f)]
    [InlineData(-1, 8, 1f)]
    [InlineData(3, 0, 1f)]
    public void ExtractionProgress_RunsFromZeroToOne(double left, double total, float expected) =>
        Assert.Equal(expected, RaidHudText.ExtractionProgress(left, total), 3);

    [Fact]
    public void ExtractionLine_NamesThePoint_AndCountsWholeSeconds()
    {
        Assert.Equal("Extracting at north gate: 5", RaidHudText.ExtractionLine("north_gate", 4.2));
        Assert.Equal("Extracting: 0", RaidHudText.ExtractionLine(null, -1));
    }

    [Theory]
    [InlineData(RaidExit.Extracted, "Extracted")]
    [InlineData(RaidExit.Died, "Killed")]
    [InlineData(RaidExit.MissingInAction, "Missing in action")]
    [InlineData(RaidExit.Aborted, "Raid called off")]
    public void ExitCard_HasATitleAndALine(RaidExit exit, string title)
    {
        var (cardTitle, detail) = RaidHudText.ExitCard(exit, null);

        Assert.Equal(title, cardTitle);
        Assert.False(string.IsNullOrEmpty(detail));
    }

    [Fact]
    public void ExitCard_ForAnExtraction_SaysWhere()
    {
        Assert.Equal("You made it out at north gate.", RaidHudText.ExitCard(RaidExit.Extracted, "north_gate").Detail);
        Assert.Equal("You made it out.", RaidHudText.ExitCard(RaidExit.Extracted, "").Detail);
    }
}
