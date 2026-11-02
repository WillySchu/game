using RaidZero.Backend.Raids;
using static Game.Backend.Tests.RaidFixture;

namespace Game.Backend.Tests;

/// <summary>
/// Game servers that report in (POST /v1/server/ready) only get new raids while their reports are recent, for
/// their map, and for clients on their network code. Servers that never report keep the old behaviour, which
/// the rest of the raid tests cover.
/// </summary>
public sealed class RaidServerReadyTests
{
    private static string ServerOf(ServerResponse ticket) => Body(ticket).GetProperty("server").GetProperty("host").GetString()!;

    [Fact]
    public async Task AReadyServer_GetsTicketsForItsMap()
    {
        using var f = new RaidFixture();
        await f.StartAsync();
        Assert.Equal(200, (await f.ReadyAsync()).StatusCode);

        var ticket = await f.Raids.CreateTicketAsync(await f.PlayerAsync("Sam"), Map);

        Assert.Equal(200, ticket.StatusCode);
    }

    [Fact]
    public async Task AReadyServer_DoesntOpenRaidsOnAnotherMap()
    {
        using var f = new RaidFixture();
        await f.StartAsync();
        await f.ReadyAsync(map: "port_townsend");

        var ticket = await f.Raids.CreateTicketAsync(await f.PlayerAsync("Sam"), Map);

        Assert.Equal(503, ticket.StatusCode);
        Assert.Equal("no_server_available", Error(ticket));
    }

    [Fact]
    public async Task AServerThatStoppedReporting_GetsNoNewRaids_UntilItReportsAgain()
    {
        using var f = new RaidFixture();
        await f.StartAsync();
        var sam = await f.PlayerAsync("Sam");
        await f.ReadyAsync();

        f.Time.Now = f.Time.Now.AddSeconds(31);
        var quiet = await f.Raids.CreateTicketAsync(sam, Map);
        await f.ReadyAsync();
        var back = await f.Raids.CreateTicketAsync(sam, Map);

        Assert.Equal("no_server_available", Error(quiet));
        Assert.Contains("reported in", Body(quiet).GetProperty("message").GetString());
        Assert.Equal(200, back.StatusCode);
    }

    [Fact]
    public async Task ANewRaid_GoesToTheFirstServerThatsReady()
    {
        using var f = new RaidFixture(serverIds: ["one", "two", "three"]);
        await f.StartAsync();
        await f.ReadyAsync(server: "one");
        await f.ReadyAsync(server: "two", map: "port_townsend");
        await f.ReadyAsync(server: "three");
        f.Time.Now = f.Time.Now.AddSeconds(20);
        await f.ReadyAsync(server: "two", map: "port_townsend");
        await f.ReadyAsync(server: "three");
        f.Time.Now = f.Time.Now.AddSeconds(15); // "one" last reported 35 seconds ago

        var greybox = await f.Raids.CreateTicketAsync(await f.PlayerAsync("Sam"), Map);
        var townsend = await f.Raids.CreateTicketAsync(await f.PlayerAsync("Ann"), "port_townsend");

        Assert.Equal("three.test", ServerOf(greybox));
        Assert.Equal("two.test", ServerOf(townsend));
    }

    [Fact]
    public async Task ServersThatNeverReport_AreStillUsed_AlongsideOnesThatDo()
    {
        using var f = new RaidFixture(serverIds: ["reports", "legacy"]);
        await f.StartAsync();
        await f.ReadyAsync(server: "reports", map: "port_townsend");

        var ticket = await f.Raids.CreateTicketAsync(await f.PlayerAsync("Sam"), Map);

        Assert.Equal("legacy.test", ServerOf(ticket));
    }

    [Fact]
    public async Task AClientsNetworkCode_MustMatchTheServers()
    {
        using var f = new RaidFixture();
        await f.StartAsync();
        var sam = await f.PlayerAsync("Sam");
        await f.ReadyAsync(networkCode: "abc123");

        var stale = await f.Raids.CreateTicketAsync(sam, Map, "old999");
        var current = await f.Raids.CreateTicketAsync(sam, Map, "abc123");

        Assert.Equal(409, stale.StatusCode);
        Assert.Equal("version_mismatch", Error(stale));
        Assert.Equal("abc123", Body(stale).GetProperty("servers")[0].GetString());
        Assert.Equal(200, current.StatusCode);
    }

    [Fact]
    public async Task ANetworkCode_IsOnlyCheckedWhenBothSidesSendOne()
    {
        using var f = new RaidFixture(serverIds: ["one", "two"]);
        await f.StartAsync();
        await f.ReadyAsync(server: "one");                       // reports no network code
        await f.ReadyAsync(server: "two", networkCode: "abc123");

        var withCode = await f.Raids.CreateTicketAsync(await f.PlayerAsync("Sam"), Map, "zzz000");
        var without = await f.Raids.CreateTicketAsync(await f.PlayerAsync("Ann"), Map);

        Assert.Equal("one.test", ServerOf(withCode));
        Assert.Equal("one.test", ServerOf(without)); // joins Sam's raid
    }

    [Fact]
    public async Task ARaidOnAnotherNetworkCode_IsntJoined()
    {
        using var f = new RaidFixture(serverIds: ["one", "two"]);
        await f.StartAsync();
        await f.ReadyAsync(server: "one", networkCode: "old999");
        await f.ReadyAsync(server: "two", networkCode: "abc123");
        var first = await f.Raids.CreateTicketAsync(await f.PlayerAsync("Sam"), Map, "old999");

        var second = await f.Raids.CreateTicketAsync(await f.PlayerAsync("Ann"), Map, "abc123");

        Assert.Equal("one.test", ServerOf(first));
        Assert.Equal("two.test", ServerOf(second));
    }

    [Fact]
    public async Task ClosingARaid_ForgetsTheServersReport_UntilItsNextProcessReports()
    {
        using var f = new RaidFixture();
        await f.StartAsync();
        await f.ReadyAsync();
        var (raidId, _) = await f.EnterAsync(await f.PlayerAsync("Sam"));
        Assert.Equal(200, (await f.CloseAsync(raidId)).StatusCode);

        var whileRestarting = await f.Raids.CreateTicketAsync(await f.PlayerAsync("Ann"), Map);
        await f.ReadyAsync();
        var afterwards = await f.Raids.CreateTicketAsync(await f.PlayerAsync("Bob"), Map);

        Assert.Equal("no_server_available", Error(whileRestarting));
        Assert.Equal(200, afterwards.StatusCode);
    }

    [Fact]
    public async Task AnAbortedRaid_ForgetsTheServersReportToo()
    {
        using var f = new RaidFixture();
        await f.StartAsync();
        await f.ReadyAsync();
        await f.EnterAsync(await f.PlayerAsync("Sam"));

        f.Time.Now = f.Time.Now.AddSeconds(61); // no heartbeats: the lease runs out
        Assert.Equal(1, await f.Raids.ReapAsync());
        var ticket = await f.Raids.CreateTicketAsync(await f.PlayerAsync("Ann"), Map);

        Assert.Equal("no_server_available", Error(ticket));
    }

    [Fact]
    public async Task ARunningRaid_KeepsTakingPlayersWhileItsServerIsBusy()
    {
        // Once its first player joins, a server stops reporting ready; its heartbeats keep the raid alive.
        using var f = new RaidFixture();
        await f.StartAsync();
        await f.ReadyAsync();
        var (raidId, _) = await f.EnterAsync(await f.PlayerAsync("Sam"));

        f.Time.Now = f.Time.Now.AddSeconds(50);
        await f.Raids.HeartbeatAsync(ServerId, raidId);
        var (annRaid, _) = await f.TicketAsync(await f.PlayerAsync("Ann"));

        Assert.Equal(raidId, annRaid);
    }

    [Fact]
    public async Task APendingRaidOnAServerThatWentQuiet_TakesNoMoreTickets()
    {
        using var f = new RaidFixture();
        await f.StartAsync();
        await f.ReadyAsync();
        await f.TicketAsync(await f.PlayerAsync("Sam")); // nobody has joined: the raid is pending

        f.Time.Now = f.Time.Now.AddSeconds(31);
        var ticket = await f.Raids.CreateTicketAsync(await f.PlayerAsync("Ann"), Map);

        Assert.Equal("no_server_available", Error(ticket));
    }

    [Theory]
    [InlineData("Not A Map", null)]
    [InlineData("", null)]
    [InlineData("greybox_01", "line\nbreak")]
    public async Task BadReports_AreRefused(string map, string? networkCode)
    {
        using var f = new RaidFixture();
        await f.StartAsync();

        var response = await f.Raids.ReportReadyAsync(ServerId, new RaidService.ReadyRequest(map, null, networkCode), Catalog.Version);

        Assert.Equal(422, response.StatusCode);
    }

    [Fact]
    public async Task TheAdminView_ListsEveryServerWithItsReportAndRaid()
    {
        using var f = new RaidFixture(serverIds: ["one", "two"]);
        await f.StartAsync();
        await f.ReadyAsync(server: "one", networkCode: "abc123");
        var (raidId, _) = await f.TicketAsync(await f.PlayerAsync("Sam"));

        var servers = await f.Raids.ListServersAsync();

        Assert.Equal(["one", "two"], servers.Select(s => s.Id));
        Assert.True(servers[0].Ready);
        Assert.Equal("abc123", servers[0].NetworkCode);
        Assert.Equal(raidId, servers[0].RaidId);
        Assert.False(servers[1].Ready);
        Assert.Null(servers[1].ReportedAt);
    }
}
