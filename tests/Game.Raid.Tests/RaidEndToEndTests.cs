using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Game.Backend.Tests;
using RaidZero.Backend.Catalog;
using RaidZero.BackendApi;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;

namespace RaidZero.Raid.Tests;

/// <summary>
/// A whole raid as the game server will run it: players join through the backend client, the raid's
/// inventory decides what happens in between, and the backend ends up with the right stashes.
/// </summary>
public class RaidEndToEndTests
{
    private static async Task<(string RaidId, string JoinToken)> TicketAsync(ApiHost api, string playerToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/me/raid-tickets") { Content = JsonContent.Create(new { map_id = "greybox_01" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", playerToken);
        using var response = await api.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("raid_id").GetString()!, body.GetProperty("join_token").GetString()!);
    }

    private static async Task<string> CharacterJsonAsync(ApiHost api, string playerToken)
    {
        using var response = await api.GetAsync("/v1/me/profile", playerToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("character").GetRawText();
    }

    private static string Slot(ItemNode parent, string slot) => parent.Children.Single(c => c.Slot == slot).Id;

    [Fact]
    public async Task ARaid_WithLootingABodyAndACrate_EndsUpInTheRightStashes()
    {
        await using var api = await ApiHost.StartAsync();
        var catalog = CatalogLoader.LoadFile(TestPaths.CatalogPath);
        var (samId, samToken) = await api.LoginAsync("Sam");
        var (alexId, alexToken) = await api.LoginAsync("Alex");
        var (raidId, samTicket) = await TicketAsync(api, samToken);
        var (_, alexTicket) = await TicketAsync(api, alexToken);

        var outbox = Path.Combine(Path.GetTempPath(), "raidzero-tests", "outbox-" + Guid.NewGuid().ToString("N"));
        using var transport = new HttpClientTransport(api.Client.BaseAddress!.ToString(), TimeSpan.FromSeconds(10));
        using var backend = new BackendClient(new BackendSettings(api.Client.BaseAddress!.ToString(), ApiHost.ServerToken, outbox), catalog.Version,
            new BackendClientOptions { RetryInitialDelay = TimeSpan.FromMilliseconds(10) }, transport, new QuietLog());
        backend.Start();
        var raid = new RaidInventoryService(catalog);

        // Both connect; the server puts their gear into the raid. A crate holds a graphics card.
        var sam = await backend.JoinAsync(raidId, samTicket);
        var alex = await backend.JoinAsync(raidId, alexTicket);
        Assert.True(sam.Succeeded && alex.Succeeded, $"{sam} / {alex}");
        raid.AddPlayer(sam.PlayerId, sam.Loadout);
        raid.AddPlayer(alex.PlayerId, alex.Loadout);
        var gpu = new ItemNode { Id = Uuid7.NewId(), DefId = "loot_gpu", Slot = SlotNames.Grid, X = 0, Y = 0 };
        var crate = new ItemNode { Id = Uuid7.NewId(), DefId = "crate_wooden", Children = [gpu] };
        raid.AddContainer(crate);

        // Sam fires three rounds, loots the crate, and Alex drops his medkit before being killed.
        var samCharacter = raid.Snapshot(raid.CharacterOf(samId))!;
        var samRounds = samCharacter.Children.Single(c => c.Slot == "primary").Walk().Single(n => n.DefId == "ammo_545_ps").Id;
        Assert.True(raid.Consume(samId, samRounds, 3).Success);
        Assert.True(raid.Open(samId, crate.Id).Success);
        Assert.True(raid.PickUp(samId, gpu.Id).Success);

        var alexCharacter = raid.Snapshot(raid.CharacterOf(alexId))!;
        var alexRifle = Slot(alexCharacter, "primary");
        var alexMedkit = alexCharacter.Walk().Single(n => n.DefId == "medkit_basic").Id;
        var drop = raid.Drop(alexId, alexMedkit);
        Assert.True(drop.Success, drop.ToString());
        raid.Kill(alexId);
        backend.ReportDied(alexId);

        // Sam loots Alex's rifle from the body and the medkit from the ground, then extracts.
        Assert.True(raid.Open(samId, alexCharacter.Id).Success);
        var rifle = raid.PickUp(samId, alexRifle);
        Assert.True(rifle.Success, rifle.ToString());
        var medkit = raid.PickUp(samId, alexMedkit);
        Assert.True(medkit.Success, medkit.ToString());
        Assert.Equal(drop.CreatedRoots, medkit.RemovedRoots);
        backend.ReportExtracted(samId, raid.Extract(samId), "north_gate");
        await Wait.ForAsync(backend.EndRaidAsync());

        // Sam's stash has what he carried out, under the same IDs; Alex has nothing left on him.
        var samNow = await CharacterJsonAsync(api, samToken);
        Assert.Contains(gpu.Id, samNow);
        Assert.Contains(alexRifle, samNow);
        Assert.Contains(alexMedkit, samNow);
        Assert.Contains("\"quantity\":27", samNow);
        var alexNow = JsonDocument.Parse(await CharacterJsonAsync(api, alexToken)).RootElement;
        Assert.Empty(alexNow.GetProperty("children").EnumerateArray());

        backend.Dispose();
        Directory.Delete(outbox, recursive: true);
    }

    private sealed class QuietLog : IBackendLog
    {
        public void Write(BackendLogLevel level, string message)
        {
        }
    }

    private static class Wait
    {
        public static async Task ForAsync(Task task)
        {
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) != task)
            {
                throw new TimeoutException("The raid didn't finish ending.");
            }

            await task;
        }
    }
}
