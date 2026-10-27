using System.Collections.Concurrent;
using System.Text.Json;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;

namespace RaidZero.BackendApi.Tests;

/// <summary>The client against a fake backend, where the test controls every answer and failure.</summary>
public sealed class BackendClientTests : IDisposable
{
    private const string Catalog = "test-catalog";

    private readonly TempFolder _outbox = new();
    private readonly FakeBackend _backend = new();
    private readonly CapturingLog _log = new();
    private readonly List<BackendClient> _clients = [];
    private readonly string _raidId = Uuid7.NewId();

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        _outbox.Dispose();
    }

    private static BackendClientOptions Fast() => new()
    {
        HeartbeatInterval = TimeSpan.FromMilliseconds(30),
        RequestTimeout = TimeSpan.FromSeconds(2),
        RetryInitialDelay = TimeSpan.FromMilliseconds(5),
        RetryMaxDelay = TimeSpan.FromMilliseconds(20),
        JoinAttempts = 3,
    };

    private BackendClient Client(IBackendTransport? transport = null, BackendClientOptions? options = null, bool start = true)
    {
        var client = new BackendClient(new BackendSettings("http://backend.test", "server-token", _outbox.Path), Catalog, options ?? Fast(), transport ?? _backend, _log);
        _clients.Add(client);
        if (start)
        {
            client.Start();
        }

        return client;
    }

    private async Task<(BackendClient Client, JoinResult Joined)> JoinedAsync(string player = "p1")
    {
        var client = Client();
        var joined = await client.JoinAsync(_raidId, "token-" + player);
        Assert.True(joined.Succeeded, joined.ToString());
        return (client, joined);
    }

    private static ItemNode Tree(string player) => new() { Id = "char-" + player, DefId = "character" };

    private string JournalText() => File.ReadAllText(Path.Combine(_outbox.Path, _raidId + ".jsonl"));

    // ---- Joining ----

    [Fact]
    public async Task Join_SendsTheServersCredentialsAndReturnsTheLoadout()
    {
        var (_, joined) = await JoinedAsync();

        var request = Assert.Single(_backend.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal($"/v1/server/raids/{_raidId}/join", request.Path);
        Assert.Equal("Bearer server-token", request.Headers["Authorization"]);
        Assert.Equal(Catalog, request.Headers["X-Catalog-Version"]);
        Assert.StartsWith("join-", request.Headers["Idempotency-Key"]);
        Assert.Equal("token-p1", JsonDocument.Parse(request.Body).RootElement.GetProperty("join_token").GetString());

        Assert.Equal("p1", joined.PlayerId);
        Assert.Equal("Sam", joined.DisplayName);
        Assert.False(joined.IsReconnect);
        Assert.Equal("char-p1", joined.Loadout.Id);
        var bag = joined.Loadout.Children.Single(c => c.Slot == SlotNames.Backpack);
        var ammo = Assert.Single(bag.Children);
        Assert.Equal((1, 2, true, 30), (ammo.X!.Value, ammo.Y!.Value, ammo.Rotated, ammo.Quantity));
        Assert.Equal(0.75, joined.Loadout.Children.Single(c => c.Slot == SlotNames.Armor).Durability);
    }

    [Fact]
    public async Task Join_RetriesNetworkFailures_WithTheSameKey()
    {
        int calls = 0;
        _backend.Handler = request => ++calls < 3 ? throw new HttpRequestException("connection refused") : FakeBackend.JoinAnswer("p1");

        var (_, joined) = await JoinedAsync();

        Assert.Equal(3, _backend.Requests.Count);
        Assert.Single(_backend.Requests.Select(r => r.Headers["Idempotency-Key"]).Distinct());
        Assert.Equal("p1", joined.PlayerId);
    }

    [Fact]
    public async Task Join_TheBackendsRefusal_IsPassedOnWithoutRetrying()
    {
        _backend.Handler = _ => FakeBackend.Error(410, "ticket_expired");
        var client = Client();

        var joined = await client.JoinAsync(_raidId, "token");

        Assert.False(joined.Succeeded);
        Assert.Equal("ticket_expired", joined.Error);
        Assert.Single(_backend.Requests);
        Assert.Null(client.RaidId);
    }

    [Fact]
    public async Task Join_GivesUpAfterTheConfiguredAttempts()
    {
        _backend.Handler = _ => FakeBackend.Error(503, "unavailable");
        var client = Client();

        var joined = await client.JoinAsync(_raidId, "token");

        Assert.Equal("backend_unreachable", joined.Error);
        Assert.Equal(3, _backend.Requests.Count);
    }

    [Theory]
    [InlineData("../../etc/passwd", "token")]
    [InlineData("not-a-raid", "token")]
    [InlineData(null, "token")]
    [InlineData("0192f3a1-0000-7000-8000-000000000000", "")]
    public async Task Join_RefusesWhatTheConnectingPlayerSent_WithoutCallingTheBackend(string? raidId, string token)
    {
        var joined = await Client().JoinAsync(raidId!, token);

        Assert.Equal("invalid_request", joined.Error);
        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public async Task Join_TheFirstPlayerSetsTheRaid_AndOtherRaidsAreRefused()
    {
        var (client, _) = await JoinedAsync();

        var other = await client.JoinAsync(Uuid7.NewId(), "token-p2");

        Assert.Equal(_raidId, client.RaidId);
        Assert.Equal("wrong_raid", other.Error);
        Assert.Single(_backend.Requests);
    }

    [Fact]
    public async Task Join_TheSameTicketAgain_IsAReconnect()
    {
        var (client, _) = await JoinedAsync();

        var again = await client.JoinAsync(_raidId, "token-p1");

        Assert.True(again.Succeeded);
        Assert.True(again.IsReconnect);
        Assert.Single(_backend.To("/join").Select(r => r.Headers["Idempotency-Key"]).Distinct());
    }

    [Fact]
    public async Task Join_AfterThePlayerLeft_IsRefused()
    {
        var (client, joined) = await JoinedAsync();
        client.ReportDied(joined.PlayerId);

        var again = await client.JoinAsync(_raidId, "token-p1");

        Assert.Equal("already_left_raid", again.Error);
    }

    // ---- Reports and the outbox ----

    [Fact]
    public async Task Reports_AreOnDiskBeforeTheCallReturns()
    {
        var (client, joined) = await JoinedAsync();
        _backend.Handler = _ => FakeBackend.Error(503, "unavailable");

        client.ReportExtracted(joined.PlayerId, joined.Loadout, "north_gate");

        var report = JsonDocument.Parse(JournalText().Split('\n')[0]).RootElement;
        Assert.Equal("report", report.GetProperty("t").GetString());
        Assert.Equal("extracted", report.GetProperty("kind").GetString());
        var body = JsonDocument.Parse(report.GetProperty("body").GetString()!).RootElement;
        Assert.Equal("p1", body.GetProperty("player_id").GetString());
        Assert.Equal("north_gate", body.GetProperty("extract_point").GetString());
        Assert.Equal("char-p1", body.GetProperty("inventory").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Reports_RetryUntilDelivered_WithTheSameKeyAndBody()
    {
        var (client, joined) = await JoinedAsync();
        int outcomeCalls = 0;
        _backend.Handler = request => request.Path.EndsWith("/outcomes", StringComparison.Ordinal) && ++outcomeCalls <= 3
            ? throw new TaskCanceledException("timed out")
            : FakeBackend.Ok();

        client.ReportExtracted(joined.PlayerId, joined.Loadout);
        await Wait.ForAsync(client.EndRaidAsync(), "the raid to end");

        var outcomes = _backend.To("/outcomes");
        Assert.Equal(4, outcomes.Count);
        Assert.Single(outcomes.Select(r => r.Headers["Idempotency-Key"]).Distinct());
        Assert.Single(outcomes.Select(r => r.Body).Distinct());
        Assert.Single(_backend.To("/close"));
        Assert.Empty(_outbox.Journals());
        Assert.Equal(0, client.PendingReports);
    }

    [Fact]
    public async Task TheClose_WaitsForEveryOutcome()
    {
        var client = Client();
        var p1 = await client.JoinAsync(_raidId, "token-p1");
        _backend.Handler = request => request.Path.EndsWith("/join", StringComparison.Ordinal) ? FakeBackend.JoinAnswer("p2") : FakeBackend.Ok();
        var p2 = await client.JoinAsync(_raidId, "token-p2");

        int failures = 0;
        _backend.Handler = request => request.Path.EndsWith("/outcomes", StringComparison.Ordinal) && ++failures <= 5
            ? FakeBackend.Error(502, "bad_gateway")
            : FakeBackend.Ok();

        client.ReportExtracted(p1.PlayerId, p1.Loadout);
        client.ReportDied(p2.PlayerId);
        await Wait.ForAsync(client.EndRaidAsync(), "the raid to end");

        var order = _backend.Requests.Where(r => !r.Path.EndsWith("/heartbeat", StringComparison.Ordinal)).Select(r => r.Path.Split('/').Last()).ToList();
        Assert.Equal("close", order.Last());
        Assert.Single(order, p => p == "close");
        var lastOutcome = order.LastIndexOf("outcomes");
        Assert.True(lastOutcome < order.IndexOf("close"), string.Join(", ", order));
        Assert.Equal(new[] { "p1", "p2" }, _backend.To("/outcomes").Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("player_id").GetString()).Distinct().ToArray());
    }

    [Fact]
    public async Task ARefusedReport_IsNotRetried_IsAnnounced_AndKeptForAPerson()
    {
        var (client, joined) = await JoinedAsync();
        var rejections = new ConcurrentQueue<ReportRejectedEvent>();
        client.ReportRejected += rejections.Enqueue;
        _backend.Handler = request => request.Path.EndsWith("/outcomes", StringComparison.Ordinal)
            ? FakeBackend.Error(409, "item_not_in_raid_pool", new { item_id = "gpu" })
            : FakeBackend.Ok();

        client.ReportExtracted(joined.PlayerId, joined.Loadout);
        await Wait.ForAsync(client.EndRaidAsync(), "the raid to end");
        await Wait.UntilAsync(() => !rejections.IsEmpty, "the rejection to be announced");

        Assert.Single(_backend.To("/outcomes"));
        var rejection = Assert.Single(rejections);
        Assert.Equal(("p1", "extracted", 409, "item_not_in_raid_pool"), (rejection.PlayerId, rejection.Kind, rejection.StatusCode, rejection.Error));
        Assert.Equal(new[] { _raidId + ".rejected.jsonl" }, _outbox.Journals());
        Assert.Contains(_log.Lines, l => l.Level == BackendLogLevel.Error && l.Message.Contains("won't be retried", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWrongTokenOrCatalog_KeepsTheReportWaiting_InsteadOfDroppingIt()
    {
        var (client, joined) = await JoinedAsync();
        int calls = 0;
        _backend.Handler = request => !request.Path.EndsWith("/outcomes", StringComparison.Ordinal) ? FakeBackend.Ok() : ++calls switch
        {
            1 => new BackendResponse(401, ""),
            2 => FakeBackend.Error(409, "catalog_mismatch"),
            3 => FakeBackend.Error(403, "forbidden"),
            _ => FakeBackend.Ok(),
        };

        client.ReportExtracted(joined.PlayerId, joined.Loadout);
        await Wait.ForAsync(client.EndRaidAsync(), "the raid to end");

        Assert.Equal(4, _backend.To("/outcomes").Count);
        Assert.Empty(_outbox.Journals());
        Assert.Contains(_log.Lines, l => l.Level == BackendLogLevel.Error && l.Message.Contains("HTTP 401", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANewRun_ResendsWhatTheLastOneLeft_WithTheSameKeys()
    {
        var down = new FakeBackend { Handler = r => r.Path.EndsWith("/join", StringComparison.Ordinal) ? FakeBackend.JoinAnswer("p1") : throw new HttpRequestException("down") };
        var first = Client(down);
        var joined = await first.JoinAsync(_raidId, "token-p1");
        first.ReportExtracted(joined.PlayerId, joined.Loadout);
        await Wait.UntilAsync(() => down.To("/outcomes").Count > 0, "a first delivery attempt");
        first.Dispose();
        var sentBefore = down.To("/outcomes")[0];

        var second = Client();
        await Wait.UntilAsync(() => _outbox.Journals().Length == 0, "the journal to be delivered and removed");

        var resent = Assert.Single(_backend.Requests);
        Assert.Equal(sentBefore.Path, resent.Path);
        Assert.Equal(sentBefore.Headers["Idempotency-Key"], resent.Headers["Idempotency-Key"]);
        Assert.Equal(sentBefore.Body, resent.Body);
        Assert.Null(second.RaidId);
    }

    [Fact]
    public async Task ANewRun_SurvivesALineCutShortByACrash()
    {
        Directory.CreateDirectory(_outbox.Path);
        var report = """{"t":"report","key":"k1","kind":"died","player_id":"p1","path":"/v1/server/raids/{RAID}/outcomes","body":"{\"player_id\":\"p1\",\"outcome\":\"died\"}"}""".Replace("{RAID}", _raidId);
        await File.WriteAllTextAsync(Path.Combine(_outbox.Path, _raidId + ".jsonl"), report + "\n" + """{"t":"deliv""");
        _backend.Handler = r => r.Path.EndsWith("/join", StringComparison.Ordinal) ? FakeBackend.JoinAnswer("p2") : throw new HttpRequestException("still down");

        var client = Client();
        await Wait.UntilAsync(() => _backend.To("/outcomes").Count > 0, "the resend");

        // The same raid carries on in this run, and its new records start on a line of their own.
        var joined = await client.JoinAsync(_raidId, "token-p2");
        client.ReportDied(joined.PlayerId);

        var lines = JournalText().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("""{"t":"deliv""", lines[1]);
        Assert.Equal("k1", JsonDocument.Parse(lines[0]).RootElement.GetProperty("key").GetString());
        Assert.Equal("p2", JsonDocument.Parse(lines[2]).RootElement.GetProperty("player_id").GetString());
        Assert.Equal("k1", _backend.To("/outcomes")[0].Headers["Idempotency-Key"]);
        Assert.Contains(_log.Lines, l => l.Message.Contains("incomplete last line", StringComparison.Ordinal));
    }

    // ---- Heartbeats and the raid's end ----

    [Fact]
    public async Task Heartbeats_RunFromTheFirstJoin_UntilTheCloseIsAnswered()
    {
        var (client, _) = await JoinedAsync();
        await Wait.UntilAsync(() => _backend.To("/heartbeat").Count >= 2, "heartbeats");
        Assert.Null(_backend.To("/heartbeat")[0].Headers.GetValueOrDefault("Idempotency-Key"));

        await Wait.ForAsync(client.EndRaidAsync(), "the raid to end");
        await Task.Delay(100);
        var afterClose = _backend.To("/heartbeat").Count;
        await Task.Delay(150);

        Assert.Equal(afterClose, _backend.To("/heartbeat").Count);
    }

    [Fact]
    public async Task AnAbortedRaid_IsAnnouncedOnce_AndTakesNoOneElse()
    {
        var (client, _) = await JoinedAsync();
        var aborted = new ConcurrentQueue<RaidAbortedEvent>();
        client.RaidAborted += aborted.Enqueue;
        _backend.Handler = _ => FakeBackend.Error(409, "raid_not_active", new { state = "aborted" });

        await Wait.UntilAsync(() => !aborted.IsEmpty, "the abort");
        var heartbeats = _backend.To("/heartbeat").Count;
        await Task.Delay(150);
        var late = await client.JoinAsync(_raidId, "token-p2");

        Assert.Equal(_raidId, Assert.Single(aborted).RaidId);
        Assert.Equal(heartbeats, _backend.To("/heartbeat").Count);
        Assert.Equal("raid_aborted", late.Error);
    }

    [Fact]
    public async Task HeartbeatFailures_DontEndTheRaid()
    {
        var (client, _) = await JoinedAsync();
        var aborted = new ConcurrentQueue<RaidAbortedEvent>();
        client.RaidAborted += aborted.Enqueue;
        _backend.Handler = _ => throw new HttpRequestException("down");

        await Wait.UntilAsync(() => _backend.To("/heartbeat").Count >= 3, "several failed heartbeats");
        _backend.Handler = FakeBackend.DefaultHandler;
        var count = _backend.To("/heartbeat").Count;
        await Wait.UntilAsync(() => _backend.To("/heartbeat").Count > count + 1, "heartbeats to carry on");

        Assert.Empty(aborted);
        Assert.Single(_log.Lines, l => l.Message.StartsWith("Heartbeat for raid", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Message.Contains("getting through again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EndingARaidNobodyJoined_SendsNothing()
    {
        var client = Client();

        await client.EndRaidAsync();

        Assert.Empty(_backend.Requests);
        Assert.Equal("raid_ending", (await client.JoinAsync(_raidId, "token")).Error);
    }

    // ---- Threading and misuse ----

    [Fact]
    public async Task Events_ArriveOnTheContextTheClientWasCreatedOn()
    {
        var context = new RecordingContext();
        BackendClient client;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            client = Client();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var handledOnContext = 0;
        client.ReportRejected += _ => Volatile.Write(ref handledOnContext, SynchronizationContext.Current == context ? 1 : -1);
        var joined = await client.JoinAsync(_raidId, "token-p1");
        _backend.Handler = _ => FakeBackend.Error(422, "invalid_inventory");
        client.ReportExtracted(joined.PlayerId, joined.Loadout);

        await Wait.UntilAsync(() => Volatile.Read(ref handledOnContext) != 0, "the handler to run");
        Assert.Equal(1, handledOnContext);
        Assert.True(context.Posts > 0);
    }

    [Fact]
    public async Task Misuse_FailsLoudly()
    {
        var unstarted = Client(start: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unstarted.JoinAsync(_raidId, "token"));

        var (client, joined) = await JoinedAsync();
        Assert.Throws<InvalidOperationException>(() => client.ReportDied("someone-else"));
        Assert.Throws<ArgumentNullException>(() => client.ReportExtracted(joined.PlayerId, null!));

        client.ReportDied(joined.PlayerId);
        Assert.Throws<InvalidOperationException>(() => client.ReportExtracted(joined.PlayerId, Tree("p1")));

        _backend.Handler = r => r.Path.EndsWith("/join", StringComparison.Ordinal) ? FakeBackend.JoinAnswer("p2") : FakeBackend.Ok();
        var p2 = await client.JoinAsync(_raidId, "token-p2");
        _ = client.EndRaidAsync();
        Assert.Throws<InvalidOperationException>(() => client.ReportDied(p2.PlayerId));

        client.Dispose();
        Assert.Throws<ObjectDisposedException>(() => client.ReportDied(p2.PlayerId));
    }

    /// <summary>Runs posted work on the thread pool with itself as the current context, and counts the posts.</summary>
    private sealed class RecordingContext : SynchronizationContext
    {
        private int _posts;

        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var previous = Current;
                SetSynchronizationContext(this);
                try
                {
                    d(state);
                }
                finally
                {
                    SetSynchronizationContext(previous);
                }
            });
        }
    }
}
