using Dapper;
using RaidZero.Backend.Players;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;
using static Game.Backend.Tests.RaidFixture;

namespace Game.Backend.Tests;

public sealed class RaidLifecycleTests : IDisposable
{
    private readonly RaidFixture _f = new();

    public void Dispose() => _f.Dispose();

    private static ItemNode Loot(string defId, string slot, int? x = null, int? y = null, int quantity = 1) =>
        new() { Id = Uuid7.NewId(), DefId = defId, Slot = slot, X = x, Y = y, Quantity = quantity };

    // ---- tickets ----

    [Fact]
    public async Task Ticket_ReservesAPlaceWithoutMovingGear()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");

        var response = await _f.Raids.CreateTicketAsync(sam, Map);

        Assert.Equal(200, response.StatusCode);
        var body = Body(response);
        Assert.Equal("127.0.0.1", body.GetProperty("server").GetProperty("host").GetString());
        Assert.Equal(7770, body.GetProperty("server").GetProperty("port").GetInt32());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("join_token").GetString()));
        Assert.Equal("pending", await _f.RaidStateAsync(body.GetProperty("raid_id").GetString()!));
        Assert.Equal(4, (await _f.ProfileAsync(sam)).Character.Children.Count);
    }

    [Fact]
    public async Task Ticket_LocksTheStashAndRefusesASecondTicket()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var (raidId, _) = await _f.TicketAsync(sam);
        var profile = await _f.ProfileAsync(sam);

        var second = await _f.Raids.CreateTicketAsync(sam, Map);
        var edit = await _f.Stash.ApplyAsync(sam, profile.ProfileVersion, [InventoryOp.Discard(profile.Stash.Children[0].Id)]);

        Assert.Equal(409, second.StatusCode);
        Assert.Equal("already_in_raid", Error(second));
        Assert.Equal(raidId, Body(second).GetProperty("raid_id").GetString());
        Assert.Equal(StashOpsStatus.InRaid, edit.Status);
        Assert.Equal(raidId, edit.RaidId);
    }

    [Fact]
    public async Task Ticket_ThatExpired_CanBeReplaced_AndTheOldTokenStopsWorking()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var (raidId, oldToken) = await _f.TicketAsync(sam);

        _f.Time.Now = _f.Time.Now.AddSeconds(61);
        Assert.Equal(410, (await _f.JoinAsync(raidId, oldToken)).StatusCode);
        var (sameRaid, newToken) = await _f.TicketAsync(sam);

        Assert.Equal(raidId, sameRaid);
        Assert.Equal("invalid_join_token", Error(await _f.JoinAsync(raidId, oldToken)));
        Assert.Equal(200, (await _f.JoinAsync(raidId, newToken)).StatusCode);
    }

    [Fact]
    public async Task Tickets_JoinTheOpenRaidOnlyWhenTheMapMatchesAndThereIsRoom()
    {
        using var f = new RaidFixture(maxPlayersPerRaid: 2);
        await f.StartAsync();
        var sam = await f.PlayerAsync("Sam");
        var ann = await f.PlayerAsync("Ann");
        var bob = await f.PlayerAsync("Bob");

        var (samRaid, _) = await f.TicketAsync(sam);
        var otherMap = await f.Raids.CreateTicketAsync(ann, "other_map"); // the only server is busy with greybox_01
        var (annRaid, _) = await f.TicketAsync(ann);
        var full = await f.Raids.CreateTicketAsync(bob, Map);             // two of two places taken

        Assert.Equal("no_server_available", Error(otherMap));
        Assert.Equal(samRaid, annRaid);
        Assert.Equal(503, full.StatusCode);
    }

    [Fact]
    public async Task Tickets_StopJoiningARaidOnceItsJoinWindowCloses()
    {
        using var f = new RaidFixture();
        await f.StartAsync();
        var sam = await f.PlayerAsync("Sam");
        var ann = await f.PlayerAsync("Ann");
        var bob = await f.PlayerAsync("Bob");
        var (raidId, _) = await f.EnterAsync(sam);

        // Still inside the window (120 seconds from the first join): Ann joins Sam.
        f.Time.Now = f.Time.Now.AddSeconds(119);
        var (annRaid, _) = await f.TicketAsync(ann);
        f.Time.Now = f.Time.Now.AddSeconds(2);
        var late = await f.Raids.CreateTicketAsync(bob, Map);

        Assert.Equal(raidId, annRaid);
        Assert.Equal("no_server_available", Error(late));

        // Once that raid closes, the server takes Bob into a fresh one.
        Assert.Equal(200, (await f.CloseAsync(raidId)).StatusCode);
        var (bobRaid, _) = await f.TicketAsync(bob);
        Assert.NotEqual(raidId, bobRaid);
    }

    [Fact]
    public async Task Ticket_WithNoServersConfigured_Is503()
    {
        using var f = new RaidFixture(withServer: false);
        await f.StartAsync();

        var response = await f.Raids.CreateTicketAsync(await f.PlayerAsync("Sam"), Map);

        Assert.Equal(503, response.StatusCode);
        Assert.Equal("no_server_available", Error(response));
    }

    // ---- join ----

    [Fact]
    public async Task Join_ChecksOutEverythingUnderTheCharacter()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var before = await _f.ProfileAsync(sam);
        var (raidId, token) = await _f.TicketAsync(sam);

        var joined = await _f.JoinAsync(raidId, token);

        Assert.Equal(200, joined.StatusCode);
        var loadout = Loadout(joined);
        Assert.Equal(before.Character.Id, loadout.Id);
        Assert.Equal(before.Character.Walk().Select(n => n.Id), loadout.Walk().Select(n => n.Id));

        var after = await _f.ProfileAsync(sam);
        Assert.Empty(after.Character.Children);
        Assert.Equal(before.ProfileVersion + 1, after.ProfileVersion);
        Assert.Equal("active", await _f.RaidStateAsync(raidId));
        Assert.Equal(loadout.Walk().Count() - 1, await _f.Db.ReadAsync(c => c.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM item_instances WHERE raid_id = @raidId AND state = 'in_raid' AND owner_id IS NULL", new { raidId })));
    }

    [Fact]
    public async Task Join_RejectsBadTokensExpiredTicketsAndOtherServers()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var (raidId, token) = await _f.TicketAsync(sam);

        Assert.Equal("invalid_join_token", Error(await _f.JoinAsync(raidId, "not-the-token")));
        Assert.Equal("raid_not_found", Error(await _f.JoinAsync(raidId, token, server: "another-server")));
        Assert.Equal("raid_not_found", Error(await _f.JoinAsync(Uuid7.NewId(), token)));

        _f.Time.Now = _f.Time.Now.AddSeconds(60);
        var late = await _f.JoinAsync(raidId, token);
        Assert.Equal(410, late.StatusCode);
        Assert.Equal("ticket_expired", Error(late));
    }

    [Fact]
    public async Task Join_RetriedWithTheSameKey_ReplaysWithoutASecondCheckout()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var (raidId, token) = await _f.TicketAsync(sam);

        var first = await _f.JoinAsync(raidId, token, key: "join-1");
        var retry = await _f.JoinAsync(raidId, token, key: "join-1");
        var reused = await _f.Raids.JoinAsync(ServerId, raidId, "join-1", "a-different-request", new(token));
        var newKey = await _f.JoinAsync(raidId, token, key: "join-2");

        Assert.Equal(first, retry);
        Assert.Equal("idempotency_key_reused", Error(reused));
        Assert.Equal("already_joined", Error(newKey));
        Assert.Equal(1, await _f.Db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM ledger_entries WHERE kind = 'checkout'")));
    }

    // ---- extraction ----

    [Fact]
    public async Task Extract_BringsTheGearAndNewLootHome()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var (raidId, inventory) = await _f.EnterAsync(sam);
        var gpu = Loot("loot_gpu", "grid", 0, 0);
        InSlot(inventory, "backpack").Children.Add(gpu);
        InSlot(InSlot(InSlot(inventory, "primary"), "magazine"), "rounds").Quantity = 12; // fired 18 rounds

        var response = await _f.ExtractAsync(raidId, sam, inventory);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(1, Body(response).GetProperty("items_gained").GetInt32());
        Assert.Equal(inventory.Walk().Count() - 2, Body(response).GetProperty("items_returned").GetInt32());

        var profile = await _f.ProfileAsync(sam);
        Assert.Equal(inventory.Walk().Select(n => n.Id).Order(), profile.Character.Walk().Select(n => n.Id).Order());
        Assert.Equal(12, InSlot(InSlot(InSlot(profile.Character, "primary"), "magazine"), "rounds").Quantity);
        Assert.Equal(1, await _f.Db.ReadAsync(c => c.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM item_instances WHERE id = @id AND origin = 'raid_loot' AND origin_raid_id = @raidId", new { id = gpu.Id, raidId })));
        Assert.Equal("extracted", await _f.ParticipantStateAsync(raidId, sam));

        var result = Body(await _f.Raids.GetResultAsync(sam, raidId));
        Assert.Equal("extracted", result.GetProperty("outcome").GetString());
        Assert.Equal(gpu.Id, result.GetProperty("items_gained")[0].GetString());
        Assert.Equal(0, result.GetProperty("items_lost").GetArrayLength());
    }

    [Fact]
    public async Task Extract_UnlocksTheStash()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var (raidId, inventory) = await _f.EnterAsync(sam);
        await _f.ExtractAsync(raidId, sam, inventory);

        var profile = await _f.ProfileAsync(sam);
        var edit = await _f.Stash.ApplyAsync(sam, profile.ProfileVersion, [InventoryOp.Discard(InSlot(profile.Character, "armor").Id)]);

        Assert.Equal(StashOpsStatus.Applied, edit.Status);
    }

    [Fact]
    public async Task Extract_LeavingSomethingBehind_LosesItWhenTheRaidCloses()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var (raidId, inventory) = await _f.EnterAsync(sam);
        var rig = InSlot(inventory, "rig");
        var medkit = AtCell(rig, 1, 0);
        rig.Children.Remove(medkit);

        await _f.ExtractAsync(raidId, sam, inventory);
        var close = await _f.CloseAsync(raidId);

        Assert.Equal(1, Body(close).GetProperty("items_lost").GetInt32());
        Assert.Equal("lost", await _f.ItemStateAsync(medkit.Id));
        Assert.Equal(medkit.Id, Body(await _f.Raids.GetResultAsync(sam, raidId)).GetProperty("items_lost")[0].GetString());
    }

    /// <summary>The rifle's magazine and the rig's spare trade places mid-raid; both still claim cleanly.</summary>
    [Fact]
    public async Task Extract_AfterSwappingMagazinesInTheRaid()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var (raidId, inventory) = await _f.EnterAsync(sam);
        var rifle = InSlot(inventory, "primary");
        var rig = InSlot(inventory, "rig");
        var fromRifle = InSlot(rifle, "magazine");
        var fromRig = AtCell(rig, 0, 0);

        rifle.Children.Remove(fromRifle);
        rig.Children.Remove(fromRig);
        (fromRig.Slot, fromRig.X, fromRig.Y) = ("magazine", null, null);
        (fromRifle.Slot, fromRifle.X, fromRifle.Y) = ("grid", 0, 0);
        rifle.Children.Add(fromRig);
        rig.Children.Add(fromRifle);

        var response = await _f.ExtractAsync(raidId, sam, inventory);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(fromRig.Id, InSlot(InSlot((await _f.ProfileAsync(sam)).Character, "primary"), "magazine").Id);
    }

    /// <summary>A dropped magazine still points at the rifle in the pool; a looted one takes its slot.</summary>
    [Fact]
    public async Task Extract_WithALootedMagazineWhereTheDroppedOneWas()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var (raidId, inventory) = await _f.EnterAsync(sam);
        var rifle = InSlot(inventory, "primary");
        var dropped = InSlot(rifle, "magazine");
        rifle.Children.Remove(dropped);
        var looted = Loot("mag_ak_30", "magazine");
        looted.Children.Add(Loot("ammo_545_ps", "rounds", quantity: 30));
        rifle.Children.Add(looted);

        var response = await _f.ExtractAsync(raidId, sam, inventory);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, Body(response).GetProperty("items_gained").GetInt32());
        await _f.CloseAsync(raidId);
        Assert.Equal("lost", await _f.ItemStateAsync(dropped.Id));
    }

    [Fact]
    public async Task Extract_ClaimingAnItemThatIsntInThePool_ChangesNothing()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var (raidId, inventory) = await _f.EnterAsync(sam);
        var stashMedkit = (await _f.ProfileAsync(sam)).Stash.Children.First(c => c.DefId == "medkit_basic");
        InSlot(inventory, "backpack").Children.Add(new ItemNode { Id = stashMedkit.Id, DefId = "medkit_basic", Slot = "grid", X = 0, Y = 0 });

        var response = await _f.ExtractAsync(raidId, sam, inventory);

        Assert.Equal(409, response.StatusCode);
        Assert.Equal("item_not_in_raid_pool", Error(response));
        Assert.Equal(stashMedkit.Id, Body(response).GetProperty("item_id").GetString());
        Assert.Equal("in_raid", await _f.ParticipantStateAsync(raidId, sam));
        Assert.Equal("in_raid", await _f.ItemStateAsync(InSlot(inventory, "primary").Id));
    }

    [Fact]
    public async Task Extract_TurningOneItemIntoAnother_IsRejected()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var (raidId, inventory) = await _f.EnterAsync(sam);
        AtCell(InSlot(inventory, "rig"), 1, 0).DefId = "loot_fuse"; // the medkit, same size

        var response = await _f.ExtractAsync(raidId, sam, inventory);

        Assert.Equal("item_mismatch", Error(response));
    }

    [Fact]
    public async Task Extract_ATreeThatBreaksTheRules_Is422()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var (raidId, inventory) = await _f.EnterAsync(sam);
        AtCell(InSlot(inventory, "rig"), 1, 0).X = 0; // medkit onto the magazine

        var response = await _f.ExtractAsync(raidId, sam, inventory);

        Assert.Equal(422, response.StatusCode);
        Assert.Equal("invalid_inventory", Error(response));
        Assert.Equal(InventoryErrorCodes.Overlap, Body(response).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Extract_RootedAnywhereButTheCharacter_Is422()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var (raidId, inventory) = await _f.EnterAsync(sam);
        inventory.Id = Uuid7.NewId();

        Assert.Equal("invalid_inventory", Error(await _f.ExtractAsync(raidId, sam, inventory)));
    }

    [Fact]
    public async Task Outcome_RetriedWithTheSameKey_Replays_ButASecondOutcomeIsRefused()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var (raidId, inventory) = await _f.EnterAsync(sam);

        var first = await _f.ExtractAsync(raidId, sam, inventory, key: "outcome-1");
        var retry = await _f.ExtractAsync(raidId, sam, inventory, key: "outcome-1");
        var second = await _f.DieAsync(raidId, sam, key: "outcome-2");

        Assert.Equal(first, retry);
        Assert.Equal("participant_not_in_raid", Error(second));
        Assert.Equal(1, await _f.Db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM ledger_entries WHERE kind = 'extract'")));
    }

    // ---- death and other players ----

    [Fact]
    public async Task Death_LeavesTheGearForSomeoneElseToExtract()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var ann = await _f.PlayerAsync("Ann");
        var (raidId, samGear) = await _f.EnterAsync(sam);
        var (annRaid, annGear) = await _f.EnterAsync(ann);
        Assert.Equal(raidId, annRaid);

        Assert.Equal(200, (await _f.DieAsync(raidId, sam)).StatusCode);

        // Ann takes Sam's rifle, magazine and all, and carries it out in her backpack.
        var rifle = InSlot(samGear, "primary");
        (rifle.Slot, rifle.X, rifle.Y) = ("grid", 0, 0);
        InSlot(annGear, "backpack").Children.Add(rifle);
        var extract = await _f.ExtractAsync(raidId, ann, annGear);

        Assert.Equal(200, extract.StatusCode);
        Assert.Contains((await _f.ProfileAsync(ann)).Character.Walk(), n => n.Id == rifle.Id);

        var close = await _f.CloseAsync(raidId);
        Assert.Equal(samGear.Walk().Count() - 1 - rifle.Walk().Count(), Body(close).GetProperty("items_lost").GetInt32());

        var samResult = Body(await _f.Raids.GetResultAsync(sam, raidId));
        Assert.Equal("died", samResult.GetProperty("outcome").GetString());
        Assert.Contains(samResult.GetProperty("items_lost").EnumerateArray(), e => e.GetString() == rifle.Id);
    }

    [Fact]
    public async Task Death_UnlocksTheStashSoThePlayerCanGearUpAgain()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var (raidId, _) = await _f.EnterAsync(sam);
        await _f.DieAsync(raidId, sam);

        var profile = await _f.ProfileAsync(sam);
        var pistol = profile.Stash.Children.First(c => c.DefId == "pistol_pm");
        var equip = await _f.Stash.ApplyAsync(sam, profile.ProfileVersion, [InventoryOp.Move(pistol.Id, InventoryTarget.NamedSlot(profile.Character.Id, "secondary"))]);

        Assert.Equal(StashOpsStatus.Applied, equip.Status);
    }

    // ---- close ----

    [Fact]
    public async Task Close_KillsWhoeverIsLeftAndExpiresUnusedTickets()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var ann = await _f.PlayerAsync("Ann");
        var (raidId, samGear) = await _f.EnterAsync(sam);
        await _f.TicketAsync(ann);

        var close = await _f.CloseAsync(raidId);

        Assert.Equal(200, close.StatusCode);
        Assert.Equal(1, Body(close).GetProperty("participants_died").GetInt32());
        Assert.Equal(samGear.Walk().Count() - 1, Body(close).GetProperty("items_lost").GetInt32());
        Assert.Equal("died", await _f.ParticipantStateAsync(raidId, sam));
        Assert.Equal("expired", await _f.ParticipantStateAsync(raidId, ann));
        Assert.Equal("closed", await _f.RaidStateAsync(raidId));
        Assert.Equal("raid_not_active", Error(await _f.DieAsync(raidId, sam)));

        var (nextRaid, _) = await _f.TicketAsync(sam);
        Assert.NotEqual(raidId, nextRaid);
    }

    // ---- crash recovery ----

    [Fact]
    public async Task ALapsedLease_RefundsEveryoneInsideToWhereTheirGearWas()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var before = (await _f.ProfileAsync(sam)).Character;
        var (raidId, _) = await _f.EnterAsync(sam);

        _f.Time.Now = _f.Time.Now.AddSeconds(59);
        Assert.Equal(0, await _f.Raids.ReapAsync());
        _f.Time.Now = _f.Time.Now.AddSeconds(2);
        Assert.Equal(1, await _f.Raids.ReapAsync());

        var after = (await _f.ProfileAsync(sam)).Character;
        Assert.Equal(Layout(before), Layout(after));
        Assert.Equal("refunded", await _f.ParticipantStateAsync(raidId, sam));
        Assert.Equal("aborted", await _f.RaidStateAsync(raidId));
        Assert.Equal(0, Body(await _f.Raids.GetResultAsync(sam, raidId)).GetProperty("items_lost").GetArrayLength());
        await _f.TicketAsync(sam);
    }

    [Fact]
    public async Task ARefund_NeverClonesWhatSomeoneElseExtracted()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var ann = await _f.PlayerAsync("Ann");
        var stashMedkitsBefore = (await _f.ProfileAsync(sam)).Stash.Children.Count(c => c.DefId == "medkit_basic");
        var (raidId, samGear) = await _f.EnterAsync(sam);
        var (_, annGear) = await _f.EnterAsync(ann);

        // Ann grabs Sam's rig with its magazine, but leaves the medkit that was in it on the floor.
        var rig = InSlot(samGear, "rig");
        var medkit = AtCell(rig, 1, 0);
        rig.Children.Remove(medkit);
        (rig.Slot, rig.X, rig.Y) = ("grid", 0, 0);
        InSlot(annGear, "backpack").Children.Add(rig);
        Assert.Equal(200, (await _f.ExtractAsync(raidId, ann, annGear)).StatusCode);

        // Then the server crashes with Sam still inside.
        _f.Time.Now = _f.Time.Now.AddSeconds(61);
        Assert.Equal(1, await _f.Raids.ReapAsync());

        var samNow = await _f.ProfileAsync(sam);
        Assert.DoesNotContain(samNow.Character.Children, c => c.Slot == "rig");
        Assert.Contains(samNow.Character.Children, c => c.Slot == "primary");
        Assert.Equal(stashMedkitsBefore + 1, samNow.Stash.Children.Count(c => c.DefId == "medkit_basic"));
        Assert.Contains((await _f.ProfileAsync(ann)).Character.Walk(), n => n.Id == rig.Id);
        Assert.Equal("extracted", await _f.ParticipantStateAsync(raidId, ann));
    }

    [Fact]
    public async Task Heartbeats_KeepTheRaidAlive()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var (raidId, _) = await _f.EnterAsync(sam);

        _f.Time.Now = _f.Time.Now.AddSeconds(50);
        Assert.Equal(200, (await _f.Raids.HeartbeatAsync(ServerId, raidId)).StatusCode);
        _f.Time.Now = _f.Time.Now.AddSeconds(50);
        Assert.Equal(0, await _f.Raids.ReapAsync());
        _f.Time.Now = _f.Time.Now.AddSeconds(11);
        Assert.Equal(1, await _f.Raids.ReapAsync());
    }

    [Fact]
    public async Task ARaidNobodyJoined_IsAbortedOnceItsTicketsAndLeaseRunOut()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var (raidId, _) = await _f.TicketAsync(sam);

        _f.Time.Now = _f.Time.Now.AddSeconds(119);
        Assert.Equal(0, await _f.Raids.ReapAsync());
        _f.Time.Now = _f.Time.Now.AddSeconds(2);
        Assert.Equal(1, await _f.Raids.ReapAsync());

        Assert.Equal("expired", await _f.ParticipantStateAsync(raidId, sam));
        Assert.Equal("aborted", await _f.RaidStateAsync(raidId));
    }

    [Fact]
    public async Task Results_AreOnlyForPlayersWhoWereThere()
    {
        await _f.StartAsync();
        var sam = await _f.PlayerAsync("Sam");
        var ann = await _f.PlayerAsync("Ann");
        var (raidId, _) = await _f.TicketAsync(sam);

        Assert.Equal(404, (await _f.Raids.GetResultAsync(ann, raidId)).StatusCode);
        Assert.Equal("ticketed", Body(await _f.Raids.GetResultAsync(sam, raidId)).GetProperty("outcome").GetString());
    }

    private static List<string> Layout(ItemNode root) =>
        root.Walk().Select(n => $"{n.Id}|{n.DefId}|{n.Slot}|{n.X}|{n.Y}|{n.Rotated}|{n.Quantity}|{n.Durability}|{string.Join(",", n.Children.Select(c => c.Id).Order())}").Order().ToList();
}
