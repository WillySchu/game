using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace RaidZero.BackendApi.Tests;

/// <summary>A stand-in backend: every request is recorded, and the test decides each answer.</summary>
public sealed class FakeBackend : IBackendTransport
{
    private readonly object _gate = new();
    private readonly List<BackendRequest> _requests = [];

    /// <summary>Answers a request. Throw to simulate the network failing.</summary>
    public Func<BackendRequest, BackendResponse> Handler { get; set; } = DefaultHandler;

    public IReadOnlyList<BackendRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    public IReadOnlyList<BackendRequest> To(string pathEnd) => Requests.Where(r => r.Path.EndsWith(pathEnd, StringComparison.Ordinal)).ToList();

    public async Task<BackendResponse> SendAsync(BackendRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return Handler(request);
    }

    public void Dispose()
    {
    }

    /// <summary>Joins as player p1 with a small loadout; everything else succeeds.</summary>
    public static BackendResponse DefaultHandler(BackendRequest request) =>
        request.Path.EndsWith("/join", StringComparison.Ordinal) ? JoinAnswer("p1") : Ok();

    public static BackendResponse Ok(string body = "{}") => new(200, body);

    public static BackendResponse Error(int status, string error, object? extra = null)
    {
        var body = new JsonObject { ["error"] = error, ["message"] = $"Test says {error}." };
        if (extra is not null)
        {
            foreach (var (key, value) in JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(extra))!.AsObject())
            {
                body[key] = value?.DeepClone();
            }
        }

        return new(status, body.ToJsonString());
    }

    public static BackendResponse JoinAnswer(string playerId) => new(200, $$"""
        {
          "player_id": "{{playerId}}",
          "display_name": "Sam",
          "loadout": {
            "id": "char-{{playerId}}", "def_id": "character", "rotated": false, "quantity": 1,
            "children": [
              { "id": "bag", "def_id": "backpack_small", "slot": "backpack", "rotated": false, "quantity": 1,
                "children": [
                  { "id": "ammo", "def_id": "ammo_9mm", "slot": "grid", "x": 1, "y": 2, "rotated": true, "quantity": 30, "children": [] }
                ] },
              { "id": "vest", "def_id": "armor_vest", "slot": "armor", "rotated": false, "quantity": 1, "durability": 0.75, "children": [] }
            ]
          }
        }
        """);
}

/// <summary>Remembers every log line, so tests can check what an operator would see.</summary>
public sealed class CapturingLog : IBackendLog
{
    private readonly ConcurrentQueue<(BackendLogLevel Level, string Message)> _lines = new();

    public IReadOnlyList<(BackendLogLevel Level, string Message)> Lines => _lines.ToList();

    public void Write(BackendLogLevel level, string message) => _lines.Enqueue((level, message));
}

/// <summary>A fresh outbox directory per test.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "raidzero-tests", "outbox-" + Guid.NewGuid().ToString("N"));

    public string[] Journals() => Directory.Exists(Path) ? Directory.GetFiles(Path).Select(f => System.IO.Path.GetFileName(f)).ToArray() : [];

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }
}

public static class Wait
{
    /// <summary>Polls until the condition holds, failing the test after a few seconds.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for " + what + ".");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Awaits the task, failing the test if it takes more than a few seconds.</summary>
    public static async Task ForAsync(Task task, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) != task)
        {
            throw new TimeoutException("Timed out waiting for " + what + ".");
        }

        await task;
    }
}
