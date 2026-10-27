using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RaidZero.Shared.Ids;

namespace RaidZero.BackendApi
{
    internal enum EntryState
    {
        Pending,
        Delivered,
        Rejected,
    }

    /// <summary>One report waiting for, or having had, the backend's answer.</summary>
    internal sealed class OutboxEntry
    {
        public OutboxEntry(string key, string kind, string playerId, string path, string body)
        {
            Key = key;
            Kind = kind;
            PlayerId = playerId;
            Path = path;
            Body = body;
        }

        /// <summary>The Idempotency-Key. Fixed when the report is written, so every retry sends the same one.</summary>
        public string Key { get; }

        /// <summary>"extracted", "died" or "close".</summary>
        public string Kind { get; }

        public string PlayerId { get; }

        public string Path { get; }

        /// <summary>Stored as sent, byte for byte, because the backend hashes the body to detect a reused key.</summary>
        public string Body { get; }

        public EntryState State { get; set; }
    }

    /// <summary>
    /// Reports that must reach the backend even if the network, the backend or this process fails first.
    ///
    /// Each raid has an append-only journal, {outbox}/{raid_id}.jsonl. A report is written there and flushed to
    /// disk before <see cref="Enqueue"/> returns; later lines record the backend's answer. A sender per raid
    /// delivers them strictly in order, one at a time, so the close report always goes after every outcome
    /// (otherwise the close would kill a player whose extraction was still in flight). Failures retry with the
    /// same key until the backend answers, and a retry after a lost answer just gets the stored answer back.
    ///
    /// On startup, journals left by an earlier run are loaded and whatever they hadn't delivered is sent. A
    /// journal is deleted once everything in it is answered, or renamed to {raid_id}.rejected.jsonl if the
    /// backend refused anything, so a person can look at what was lost.
    /// </summary>
    internal sealed class Outbox : IDisposable
    {
        private const string JournalSuffix = ".jsonl";
        private const string RejectedSuffix = ".rejected.jsonl";

        private readonly string _directory;
        private readonly Func<OutboxEntry, CancellationToken, Task<CallResult>> _send;
        private readonly BackendClientOptions _options;
        private readonly IBackendLog _log;
        private readonly Action<string, OutboxEntry, CallResult> _onRejected;
        private readonly Action<string> _onSettled;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Journal> _journals = new Dictionary<string, Journal>(StringComparer.Ordinal);
        private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
        private readonly Random _random = new Random();
        private bool _disposed;

        public Outbox(
            string directory,
            Func<OutboxEntry, CancellationToken, Task<CallResult>> send,
            BackendClientOptions options,
            IBackendLog log,
            Action<string, OutboxEntry, CallResult> onRejected,
            Action<string> onSettled)
        {
            _directory = directory;
            _send = send;
            _options = options;
            _log = log;
            _onRejected = onRejected;
            _onSettled = onSettled;
        }

        /// <summary>Loads journals from earlier runs and starts sending whatever they still hold.</summary>
        public void Start()
        {
            Directory.CreateDirectory(_directory);
            foreach (var path in Directory.GetFiles(_directory, "*" + JournalSuffix))
            {
                if (path.EndsWith(RejectedSuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                var name = Path.GetFileName(path);
                var raidId = name.Substring(0, name.Length - JournalSuffix.Length);
                if (!Uuid7.IsValid(raidId))
                {
                    _log.Write(BackendLogLevel.Warning, $"Ignoring {path} in the outbox: its name isn't a raid ID.");
                    continue;
                }

                var journal = Journal.Load(path, raidId, _log);
                var pending = journal.PendingCount;
                if (pending > 0)
                {
                    _log.Write(BackendLogLevel.Info, $"Resending {pending} report(s) for raid {raidId} left by an earlier run.");
                }

                lock (_gate)
                {
                    _journals[raidId] = journal;
                }

                StartSender(journal);
            }
        }

        /// <summary>Writes the report to disk, then hands it to the raid's sender. Durable once this returns.</summary>
        public void Enqueue(string raidId, string kind, string playerId, string path, string body)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(Outbox));
                }

                bool fresh = !_journals.TryGetValue(raidId, out var journal) || journal.Finished;
                if (fresh)
                {
                    journal = Journal.Create(Path.Combine(_directory, raidId + JournalSuffix), raidId);
                    _journals[raidId] = journal;
                }

                // A journal from an earlier run is finished once it's drained. One this process writes to stays
                // open until its close report is answered, since more outcomes may still come.
                journal.Live = true;
                journal.Append(new OutboxEntry(Uuid7.NewId(), kind, playerId, path, body));
                if (fresh)
                {
                    StartSender(journal);
                }
            }
        }

        /// <summary>Completes once every report for the raid has an answer, including its close.</summary>
        public Task WhenSettled(string raidId)
        {
            lock (_gate)
            {
                return _journals.TryGetValue(raidId, out var journal) ? journal.Settled.Task : Task.CompletedTask;
            }
        }

        public int PendingCount(string raidId)
        {
            lock (_gate)
            {
                return _journals.TryGetValue(raidId, out var journal) && !journal.Finished ? journal.PendingCount : 0;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _stopping.Cancel();
                foreach (var journal in _journals.Values)
                {
                    journal.CloseFile();
                }
            }
        }

        private void StartSender(Journal journal)
        {
            var stopping = _stopping.Token;
            journal.Sender = Task.Run(() => SendLoopAsync(journal, stopping));
        }

        private async Task SendLoopAsync(Journal journal, CancellationToken ct)
        {
            try
            {
                while (true)
                {
                    OutboxEntry entry;
                    bool finished = false;
                    lock (_gate)
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        entry = journal.NextPending();
                        if (entry == null && (journal.CloseAnswered || !journal.Live))
                        {
                            FinishLocked(journal);
                            finished = true;
                        }
                    }

                    if (finished)
                    {
                        journal.Settled.TrySetResult(true);
                        _onSettled(journal.RaidId);
                        return;
                    }

                    if (entry == null)
                    {
                        await journal.Signal.WaitAsync(ct).ConfigureAwait(false);
                        continue;
                    }

                    await DeliverAsync(journal, entry, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutting down. Anything undelivered is still on disk for the next run.
            }
            catch (Exception exception)
            {
                _log.Write(BackendLogLevel.Error, $"The outbox sender for raid {journal.RaidId} stopped: {exception}");
            }
        }

        private async Task DeliverAsync(Journal journal, OutboxEntry entry, CancellationToken ct)
        {
            var delay = _options.RetryInitialDelay;
            for (int attempt = 1; ; attempt++)
            {
                var result = await _send(entry, ct).ConfigureAwait(false);
                if (result.Status == CallStatus.Succeeded)
                {
                    lock (_gate)
                    {
                        journal.Answer(entry, EntryState.Delivered, null);
                    }

                    if (attempt > 1)
                    {
                        _log.Write(BackendLogLevel.Info, $"Delivered the {entry.Kind} report for raid {journal.RaidId} after {attempt} attempts.");
                    }

                    return;
                }

                if (result.Status == CallStatus.Rejected)
                {
                    lock (_gate)
                    {
                        journal.Answer(entry, EntryState.Rejected, result);
                    }

                    _log.Write(BackendLogLevel.Error, $"The backend refused the {entry.Kind} report for raid {journal.RaidId}{(entry.PlayerId != null ? ", player " + entry.PlayerId : "")}: {result}. It won't be retried.");
                    _onRejected(journal.RaidId, entry, result);
                    return;
                }

                // Log the first failure and then every tenth, so a long outage doesn't flood the log.
                if (attempt == 1 || attempt % 10 == 0)
                {
                    var level = result.IsConfigurationProblem ? BackendLogLevel.Error : BackendLogLevel.Warning;
                    _log.Write(level, $"Couldn't deliver the {entry.Kind} report for raid {journal.RaidId} (attempt {attempt}): {result}. Retrying.");
                }

                await Task.Delay(Jitter(delay), ct).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.RetryMaxDelay.Ticks));
            }
        }

        /// <summary>Between 75% and 125% of the delay, so servers that lost the backend together don't retry in step.</summary>
        private TimeSpan Jitter(TimeSpan delay)
        {
            double factor;
            lock (_random)
            {
                factor = 0.75 + (_random.NextDouble() * 0.5);
            }

            return TimeSpan.FromTicks((long)(delay.Ticks * factor));
        }

        private void FinishLocked(Journal journal)
        {
            journal.Finished = true;
            journal.CloseFile();
            try
            {
                if (!journal.HasRejections)
                {
                    File.Delete(journal.FilePath);
                    return;
                }

                var target = Path.Combine(_directory, journal.RaidId + RejectedSuffix);
                if (File.Exists(target))
                {
                    target = Path.Combine(_directory, journal.RaidId + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + RejectedSuffix);
                }

                File.Move(journal.FilePath, target);
                _log.Write(BackendLogLevel.Warning, $"Kept the refused report(s) for raid {journal.RaidId} in {target}.");
            }
            catch (IOException exception)
            {
                _log.Write(BackendLogLevel.Warning, $"Couldn't tidy up {journal.FilePath}: {exception.Message}");
            }
        }

        /// <summary>One raid's file and what's in it.</summary>
        private sealed class Journal
        {
            private readonly List<OutboxEntry> _entries = new List<OutboxEntry>();
            private FileStream _stream;

            private Journal(string filePath, string raidId)
            {
                FilePath = filePath;
                RaidId = raidId;
            }

            public string FilePath { get; }

            public string RaidId { get; }

            public SemaphoreSlim Signal { get; } = new SemaphoreSlim(0);

            public TaskCompletionSource<bool> Settled { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Sender { get; set; }

            /// <summary>This process is still writing to it. Journals from earlier runs aren't, unless the raid continues.</summary>
            public bool Live { get; set; }

            public bool Finished { get; set; }

            public bool CloseAnswered { get; private set; }

            public bool HasRejections { get; private set; }

            public int PendingCount
            {
                get
                {
                    int count = 0;
                    foreach (var entry in _entries)
                    {
                        if (entry.State == EntryState.Pending)
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }

            public static Journal Create(string filePath, string raidId)
            {
                var journal = new Journal(filePath, raidId);
                journal.OpenFile();
                return journal;
            }

            public static Journal Load(string filePath, string raidId, IBackendLog log)
            {
                var journal = new Journal(filePath, raidId);
                var text = File.ReadAllText(filePath, Encoding.UTF8);
                var byKey = new Dictionary<string, OutboxEntry>(StringComparer.Ordinal);
                var lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    JObject record = Wire.TryParseObject(line);
                    if (record == null)
                    {
                        // The last line can be cut short by a crash mid-write. Losing it is safe: an unrecorded
                        // answer means a resend, which the backend answers from its idempotency store.
                        bool tornTail = i == lines.Length - 1;
                        log.Write(BackendLogLevel.Warning, tornTail
                            ? $"Ignoring an incomplete last line in {filePath}."
                            : $"Skipping unreadable line {i + 1} in {filePath}.");
                        continue;
                    }

                    var type = (string)record["t"];
                    var key = (string)record["key"];
                    if (type == "report" && key != null && !byKey.ContainsKey(key))
                    {
                        var entry = new OutboxEntry(key, (string)record["kind"], (string)record["player_id"], (string)record["path"], (string)record["body"]);
                        byKey[key] = entry;
                        journal._entries.Add(entry);
                    }
                    else if ((type == "delivered" || type == "rejected") && key != null && byKey.TryGetValue(key, out var answered))
                    {
                        journal.MarkAnswered(answered, type == "delivered" ? EntryState.Delivered : EntryState.Rejected);
                    }
                }

                journal.OpenFile();
                if (text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal))
                {
                    // Start new records on a fresh line, rather than gluing them to a torn one.
                    journal.WriteRaw("\n");
                }

                return journal;
            }

            public OutboxEntry NextPending()
            {
                foreach (var entry in _entries)
                {
                    if (entry.State == EntryState.Pending)
                    {
                        return entry;
                    }
                }

                return null;
            }

            public void Append(OutboxEntry entry)
            {
                WriteRecord(new JObject
                {
                    ["t"] = "report",
                    ["key"] = entry.Key,
                    ["kind"] = entry.Kind,
                    ["player_id"] = entry.PlayerId,
                    ["path"] = entry.Path,
                    ["body"] = entry.Body,
                    ["at"] = DateTime.UtcNow.ToString("o"),
                });
                _entries.Add(entry);
                Signal.Release();
            }

            public void Answer(OutboxEntry entry, EntryState state, CallResult result)
            {
                var record = new JObject
                {
                    ["t"] = state == EntryState.Delivered ? "delivered" : "rejected",
                    ["key"] = entry.Key,
                    ["at"] = DateTime.UtcNow.ToString("o"),
                };

                if (result != null)
                {
                    record["status"] = result.StatusCode;
                    record["response"] = result.Body;
                }

                // If the file's already closed (shutting down), the answer goes unrecorded and the next run
                // resends the report, which is harmless.
                if (_stream != null)
                {
                    WriteRecord(record);
                }

                MarkAnswered(entry, state);
            }

            public void CloseFile()
            {
                _stream?.Dispose();
                _stream = null;
            }

            private void MarkAnswered(OutboxEntry entry, EntryState state)
            {
                entry.State = state;
                if (entry.Kind == "close")
                {
                    CloseAnswered = true;
                }

                if (state == EntryState.Rejected)
                {
                    HasRejections = true;
                }
            }

            private void OpenFile() =>
                _stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);

            private void WriteRecord(JObject record) => WriteRaw(Wire.Compact(record) + "\n");

            private void WriteRaw(string text)
            {
                if (_stream == null)
                {
                    throw new ObjectDisposedException(FilePath);
                }

                var bytes = Encoding.UTF8.GetBytes(text);
                _stream.Write(bytes, 0, bytes.Length);

                // To the disk itself, not just the OS cache: a report must survive the machine losing power.
                _stream.Flush(true);
            }
        }
    }
}
