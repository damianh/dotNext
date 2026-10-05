using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

// Opt-in persistence diagnostics (#123).
[Collection(TestCollections.WriteAheadLog)]
public sealed class WriteAheadLogDiagnosticsTests : Test
{
    private const string TagName = "test-wal";

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task MeterReportsPhasesAndLockTimes()
    {
        var id = Guid.NewGuid().ToString();
        using var recorder = new MeasurementRecorder(id);
        var options = CreateOptions(id);
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            await wal.AppendAsync(new TestLogEntry("first"), TestToken);
            await wal.AppendAsync(new TestLogEntry("second"), TestToken);
            await wal.CommitAsync(2L, TestToken);
            await wal.WaitForApplyAsync(2L, TestToken);
            await wal.FlushAsync(TestToken);
            using var reader = await wal.ReadAsync(1L, 2L, TestToken);
            Equal(2, reader.Count);
        }

        foreach (var cause in new[] { "append", "flush" })
        {
            foreach (var phase in new[] { "pages", "data-directory", "metadata-directory", "checkpoint-slot", "checkpoint-commit" })
                Contains(("persist-phase-duration", phase, cause), recorder.Keys);

            Contains(("lock-wait-duration", "persistence", cause), recorder.Keys);
            Contains(("lock-hold-duration", "persistence", cause), recorder.Keys);
        }

        Contains(("lock-wait-duration", "append", "append"), recorder.Keys);
        Contains(("lock-wait-duration", "read", "apply"), recorder.Keys);
        Contains(("lock-wait-duration", "read", "flush"), recorder.Keys);
        Contains(("lock-wait-duration", "read", "read"), recorder.Keys);
        Contains(("lock-wait-duration", "commit", "commit"), recorder.Keys);
        All(recorder.Values, static value => True(value >= 0D));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task MeterIsSilentWithoutSubscription()
    {
        var id = Guid.NewGuid().ToString();
        using var recorder = new MeasurementRecorder(id, "entries-append-count");
        await using (var wal = new WriteAheadLog(CreateOptions(id), IStateMachine.CreateNoOp()))
        {
            await wal.AppendAsync(new TestLogEntry("payload"), TestToken);
            await wal.CommitAsync(1L, TestToken);
            await wal.FlushAsync(TestToken);
        }

        Equal(new[] { ("entries-append-count", string.Empty, string.Empty) }, recorder.Keys.Distinct());
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task EventSourceReportsPhasesWhenEnabled()
    {
        using var listener = new PersistenceListener();
        await using (var wal = new WriteAheadLog(CreateOptions(Guid.NewGuid().ToString()), IStateMachine.CreateNoOp()))
        {
            // the first persist cycle of a fresh store upgrades the checkpoint, the second one updates the slot
            await wal.AppendAsync(new TestLogEntry("first"), TestToken);
            await wal.AppendAsync(new TestLogEntry("second"), TestToken);
            await wal.CommitAsync(2L, TestToken);
            await wal.WaitForApplyAsync(2L, TestToken);
            await wal.FlushAsync(TestToken);
        }

        Contains(listener.Events, static e => e is { Name: "PersistPhase", Phase: "checkpoint-slot", Cause: "append" });
        Contains(listener.Events, static e => e is { Name: "PersistPhase", Phase: "checkpoint-slot", Cause: "flush" });
        Contains(listener.Events, static e => e is { Name: "LockWait", Phase: "persistence", Cause: "append" });
        Contains(listener.Events, static e => e is { Name: "LockHold", Phase: "persistence", Cause: "flush" });
        All(listener.Events, static e => True(e.Duration >= 0D));
    }

    private static WriteAheadLog.Options CreateOptions(string id)
        => new()
        {
            Location = GetTempPath(),
            MemoryManagement = WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
            FlushInterval = TimeSpan.Zero,
            MeasurementTags = new() { { TagName, id } },
        };

    private sealed class MeasurementRecorder : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly ConcurrentQueue<((string, string, string) Key, double Value)> measurements = new();
        private readonly string id;

        internal MeasurementRecorder(string id, params string[] instruments)
        {
            this.id = id;
            var names = instruments is [] ? ["persist-phase-duration", "lock-wait-duration", "lock-hold-duration"] : instruments;
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name is "DotNext.IO.WriteAheadLog" && names.Contains(instrument.Name))
                    l.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<double>(OnMeasurement);
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) => OnMeasurement(instrument, value, tags, state));
            listener.Start();
        }

        private void OnMeasurement(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            string? wal = null;
            string first = string.Empty, cause = string.Empty;
            foreach (var (key, tag) in tags)
            {
                switch (key)
                {
                    case TagName:
                        wal = tag as string;
                        break;
                    case "dotnext.wal.phase" or "dotnext.wal.lock":
                        first = tag as string ?? string.Empty;
                        break;
                    case "dotnext.wal.cause":
                        cause = tag as string ?? string.Empty;
                        break;
                }
            }

            if (wal == id)
                measurements.Enqueue(((instrument.Name, first, cause), value));
        }

        internal IEnumerable<(string, string, string)> Keys => measurements.Select(static m => m.Key).ToArray();

        internal IEnumerable<double> Values => measurements.Select(static m => m.Value).ToArray();

        public void Dispose() => listener.Dispose();
    }

    private sealed class PersistenceListener : EventListener
    {
        internal readonly ConcurrentQueue<(string? Name, string? Phase, string? Cause, double Duration)> Events = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name is "DotNext-IO-WriteAheadLog")
                EnableEvents(eventSource, EventLevel.Informational, (EventKeywords)0x1);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData is { EventSource.Name: "DotNext-IO-WriteAheadLog", Payload: [string first, string cause, double duration] })
                Events.Enqueue((eventData.EventName, first, cause, duration));
        }
    }
}
