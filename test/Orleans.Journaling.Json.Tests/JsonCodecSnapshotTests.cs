using System.Buffers;
using System.Text;
using System.Text.Json;
using Orleans.Journaling.Tests;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Xunit;
using static VerifyXunit.Verifier;

namespace Orleans.Journaling.Json.Tests;

/// <summary>
/// Snapshot tests pinning the JSONL on-disk wire format produced by the new <c>Json*CommandCodec</c>
/// implementations under <see cref="Orleans.Journaling.Json"/>.
/// </summary>
/// <remarks>
/// JSONL is the new default journal format (this branch flips the registration). Each test:
/// 1. writes a single operation through a real codec into a <see cref="JsonLinesJournalFormat"/> batch,
/// 2. round-trips the produced bytes through the format reader against a recording state to prove the
///    bytes still parse and apply, and
/// 3. snapshots the produced UTF-8 text via Verify.Xunit
///    (file <c>snapshots/&lt;TestName&gt;.verified.jsonl</c>). The snapshot is the canonical
///    human-readable view: any wire-format drift fails as a JSON-line text diff.
/// </remarks>
[TestCategory("BVT")]
public sealed class JsonCodecSnapshotTests
{
    private const uint SnapshotStreamId = 8;

    private static readonly JsonSerializerOptions Options = new() { TypeInfoResolver = JsonCodecTestJsonContext.Default };

    private static readonly JournalingSnapshotRecord SampleRecord = new(
        "snapshot-record",
        7,
        new JournalingSnapshotRecordTag("tag", 99));

    private static readonly JournalingSnapshotRecord SampleRecordAlt = new(
        "alt-record",
        13,
        new JournalingSnapshotRecordTag("alt-tag", 17));

    // --- Dictionary ----------------------------------------------------------------------------

    [Fact]
    public Task Dictionary_Set_Primitives() =>
        SnapshotDictionaryOperation<string, int>(
            (codec, writer) => codec.WriteSet("alpha", 42, writer),
            ["set:alpha:42"]);

    [Fact]
    public Task Dictionary_Set_Record() =>
        SnapshotDictionaryOperation<string, JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteSet("alpha", SampleRecord, writer),
            [$"set:alpha:{SampleRecord}"]);

    [Fact]
    public Task Dictionary_Remove_Primitives() =>
        SnapshotDictionaryOperation<string, int>(
            (codec, writer) => codec.WriteRemove("alpha", writer),
            ["remove:alpha"]);

    [Fact]
    public Task Dictionary_Remove_Record() =>
        SnapshotDictionaryOperation<string, JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteRemove("alpha", writer),
            ["remove:alpha"]);

    [Fact]
    public Task Dictionary_Clear_Primitives() =>
        SnapshotDictionaryOperation<string, int>(
            (codec, writer) => codec.WriteClear(writer),
            ["clear"]);

    [Fact]
    public Task Dictionary_Clear_Record() =>
        SnapshotDictionaryOperation<string, JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteClear(writer),
            ["clear"]);

    [Fact]
    public Task Dictionary_Snapshot_Primitives() =>
        SnapshotDictionaryOperation<string, int>(
            (codec, writer) => codec.WriteSnapshot([new("alpha", 1), new("beta", 2)], writer),
            ["reset:2", "set:alpha:1", "set:beta:2"]);

    [Fact]
    public Task Dictionary_Snapshot_Record() =>
        SnapshotDictionaryOperation<string, JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteSnapshot([new("alpha", SampleRecord), new("beta", SampleRecordAlt)], writer),
            ["reset:2", $"set:alpha:{SampleRecord}", $"set:beta:{SampleRecordAlt}"]);

    [Fact]
    public Task Dictionary_SnapshotEmpty_Primitives() =>
        SnapshotDictionaryOperation<string, int>(
            (codec, writer) => codec.WriteSnapshot([], writer),
            ["reset:0"]);

    [Fact]
    public Task Dictionary_SnapshotEmpty_Record() =>
        SnapshotDictionaryOperation<string, JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteSnapshot([], writer),
            ["reset:0"]);

    [Fact]
    public Task Dictionary_MultiOperation_Sequence()
    {
        var codec = new JsonDurableDictionaryCommandCodec<string, int>(Options);
        var state = new RecordingDictionaryState<string, int>(codec);
        return SnapshotMultiOp(
            codec,
            state,
            writer =>
            {
                codec.WriteSet("alpha", 1, writer);
                codec.WriteSet("beta", 2, writer);
                codec.WriteRemove("alpha", writer);
                codec.WriteSnapshot([new("beta", 2), new("gamma", 3)], writer);
            },
            ["set:alpha:1", "set:beta:2", "remove:alpha", "reset:2", "set:beta:2", "set:gamma:3"],
            () => state.Commands);
    }

    // --- List ----------------------------------------------------------------------------------

    [Fact]
    public Task List_Add_Primitives() =>
        SnapshotListOperation<string>(
            (codec, writer) => codec.WriteAdd("one", writer),
            ["add:one"]);

    [Fact]
    public Task List_Add_Record() =>
        SnapshotListOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteAdd(SampleRecord, writer),
            [$"add:{SampleRecord}"]);

    [Fact]
    public Task List_Set_Primitives() =>
        SnapshotListOperation<string>(
            (codec, writer) => codec.WriteSet(0, "updated", writer),
            ["set:0:updated"]);

    [Fact]
    public Task List_Set_Record() =>
        SnapshotListOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteSet(0, SampleRecord, writer),
            [$"set:0:{SampleRecord}"]);

    [Fact]
    public Task List_Insert_Primitives() =>
        SnapshotListOperation<string>(
            (codec, writer) => codec.WriteInsert(1, "two", writer),
            ["insert:1:two"]);

    [Fact]
    public Task List_Insert_Record() =>
        SnapshotListOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteInsert(1, SampleRecord, writer),
            [$"insert:1:{SampleRecord}"]);

    [Fact]
    public Task List_RemoveAt_Primitives() =>
        SnapshotListOperation<string>(
            (codec, writer) => codec.WriteRemoveAt(0, writer),
            ["remove:0"]);

    [Fact]
    public Task List_RemoveAt_Record() =>
        SnapshotListOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteRemoveAt(0, writer),
            ["remove:0"]);

    [Fact]
    public Task List_Clear_Primitives() =>
        SnapshotListOperation<string>(
            (codec, writer) => codec.WriteClear(writer),
            ["clear"]);

    [Fact]
    public Task List_Clear_Record() =>
        SnapshotListOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteClear(writer),
            ["clear"]);

    [Fact]
    public Task List_Snapshot_Primitives() =>
        SnapshotListOperation<string>(
            (codec, writer) => codec.WriteSnapshot(["three", "four"], writer),
            ["reset:2", "add:three", "add:four"]);

    [Fact]
    public Task List_Snapshot_Record() =>
        SnapshotListOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteSnapshot([SampleRecord, SampleRecordAlt], writer),
            ["reset:2", $"add:{SampleRecord}", $"add:{SampleRecordAlt}"]);

    [Fact]
    public Task List_SnapshotEmpty_Primitives() =>
        SnapshotListOperation<string>(
            (codec, writer) => codec.WriteSnapshot([], writer),
            ["reset:0"]);

    [Fact]
    public Task List_SnapshotEmpty_Record() =>
        SnapshotListOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteSnapshot([], writer),
            ["reset:0"]);

    [Fact]
    public Task List_MultiOperation_Sequence()
    {
        var codec = new JsonDurableListCommandCodec<string>(Options);
        var state = new RecordingListState<string>(codec);
        return SnapshotMultiOp(
            codec,
            state,
            writer =>
            {
                codec.WriteAdd("one", writer);
                codec.WriteAdd("two", writer);
                codec.WriteSet(1, "updated", writer);
                codec.WriteInsert(0, "head", writer);
                codec.WriteRemoveAt(2, writer);
                codec.WriteSnapshot(["alpha", "beta"], writer);
            },
            ["add:one", "add:two", "set:1:updated", "insert:0:head", "remove:2", "reset:2", "add:alpha", "add:beta"],
            () => state.Commands);
    }

    // --- Queue ---------------------------------------------------------------------------------

    [Fact]
    public Task Queue_Enqueue_Primitives() =>
        SnapshotQueueOperation<int>(
            (codec, writer) => codec.WriteEnqueue(10, writer),
            ["enqueue:10"]);

    [Fact]
    public Task Queue_Enqueue_Record() =>
        SnapshotQueueOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteEnqueue(SampleRecord, writer),
            [$"enqueue:{SampleRecord}"]);

    [Fact]
    public Task Queue_Dequeue_Primitives() =>
        SnapshotQueueOperation<int>(
            (codec, writer) => codec.WriteDequeue(writer),
            ["dequeue"]);

    [Fact]
    public Task Queue_Dequeue_Record() =>
        SnapshotQueueOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteDequeue(writer),
            ["dequeue"]);

    [Fact]
    public Task Queue_Clear_Primitives() =>
        SnapshotQueueOperation<int>(
            (codec, writer) => codec.WriteClear(writer),
            ["clear"]);

    [Fact]
    public Task Queue_Clear_Record() =>
        SnapshotQueueOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteClear(writer),
            ["clear"]);

    [Fact]
    public Task Queue_Snapshot_Primitives() =>
        SnapshotQueueOperation<int>(
            (codec, writer) => codec.WriteSnapshot([20, 30], writer),
            ["reset:2", "enqueue:20", "enqueue:30"]);

    [Fact]
    public Task Queue_Snapshot_Record() =>
        SnapshotQueueOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteSnapshot([SampleRecord, SampleRecordAlt], writer),
            ["reset:2", $"enqueue:{SampleRecord}", $"enqueue:{SampleRecordAlt}"]);

    [Fact]
    public Task Queue_SnapshotEmpty_Primitives() =>
        SnapshotQueueOperation<int>(
            (codec, writer) => codec.WriteSnapshot([], writer),
            ["reset:0"]);

    [Fact]
    public Task Queue_SnapshotEmpty_Record() =>
        SnapshotQueueOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteSnapshot([], writer),
            ["reset:0"]);

    [Fact]
    public Task Queue_MultiOperation_Sequence()
    {
        var codec = new JsonDurableQueueCommandCodec<int>(Options);
        var state = new RecordingQueueState<int>(codec);
        return SnapshotMultiOp(
            codec,
            state,
            writer =>
            {
                codec.WriteEnqueue(10, writer);
                codec.WriteEnqueue(20, writer);
                codec.WriteDequeue(writer);
                codec.WriteSnapshot([30, 40], writer);
            },
            ["enqueue:10", "enqueue:20", "dequeue", "reset:2", "enqueue:30", "enqueue:40"],
            () => state.Commands);
    }

    // --- Set -----------------------------------------------------------------------------------

    [Fact]
    public Task Set_Add_Primitives() =>
        SnapshotSetOperation<string>(
            (codec, writer) => codec.WriteAdd("alpha", writer),
            ["add:alpha"]);

    [Fact]
    public Task Set_Add_Record() =>
        SnapshotSetOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteAdd(SampleRecord, writer),
            [$"add:{SampleRecord}"]);

    [Fact]
    public Task Set_Remove_Primitives() =>
        SnapshotSetOperation<string>(
            (codec, writer) => codec.WriteRemove("alpha", writer),
            ["remove:alpha"]);

    [Fact]
    public Task Set_Remove_Record() =>
        SnapshotSetOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteRemove(SampleRecord, writer),
            [$"remove:{SampleRecord}"]);

    [Fact]
    public Task Set_Clear_Primitives() =>
        SnapshotSetOperation<string>(
            (codec, writer) => codec.WriteClear(writer),
            ["clear"]);

    [Fact]
    public Task Set_Clear_Record() =>
        SnapshotSetOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteClear(writer),
            ["clear"]);

    [Fact]
    public Task Set_Snapshot_Primitives() =>
        SnapshotSetOperation<string>(
            (codec, writer) => codec.WriteSnapshot(["alpha", "beta"], writer),
            ["reset:2", "add:alpha", "add:beta"]);

    [Fact]
    public Task Set_Snapshot_Record() =>
        SnapshotSetOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteSnapshot([SampleRecord, SampleRecordAlt], writer),
            ["reset:2", $"add:{SampleRecord}", $"add:{SampleRecordAlt}"]);

    [Fact]
    public Task Set_SnapshotEmpty_Primitives() =>
        SnapshotSetOperation<string>(
            (codec, writer) => codec.WriteSnapshot([], writer),
            ["reset:0"]);

    [Fact]
    public Task Set_SnapshotEmpty_Record() =>
        SnapshotSetOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteSnapshot([], writer),
            ["reset:0"]);

    [Fact]
    public Task Set_MultiOperation_Sequence()
    {
        var codec = new JsonDurableSetCommandCodec<string>(Options);
        var state = new RecordingSetState<string>(codec);
        return SnapshotMultiOp(
            codec,
            state,
            writer =>
            {
                codec.WriteAdd("alpha", writer);
                codec.WriteAdd("beta", writer);
                codec.WriteRemove("alpha", writer);
                codec.WriteSnapshot(["beta", "gamma"], writer);
            },
            ["add:alpha", "add:beta", "remove:alpha", "reset:2", "add:beta", "add:gamma"],
            () => state.Commands);
    }

    // --- Value ---------------------------------------------------------------------------------

    [Fact]
    public Task Value_Set_Primitives()
    {
        var codec = new JsonDurableValueCommandCodec<int>(Options);
        var state = new RecordingValueState<int>(codec);
        return SnapshotSingleOp(
            codec,
            state,
            writer => codec.WriteSet(42, writer),
            () => Assert.Equal(42, state.Value));
    }

    [Fact]
    public Task Value_Set_Record()
    {
        var codec = new JsonDurableValueCommandCodec<JournalingSnapshotRecord>(Options);
        var state = new RecordingValueState<JournalingSnapshotRecord>(codec);
        return SnapshotSingleOp(
            codec,
            state,
            writer => codec.WriteSet(SampleRecord, writer),
            () => Assert.Equal(SampleRecord, state.Value));
    }

    // --- State ---------------------------------------------------------------------------------

    [Fact]
    public Task State_Set_Primitives() =>
        SnapshotStateOperation<string>(
            (codec, writer) => codec.WriteSet("active", 7, writer),
            ["set:active:7"]);

    [Fact]
    public Task State_Set_Record() =>
        SnapshotStateOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteSet(SampleRecord, 7, writer),
            [$"set:{SampleRecord}:7"]);

    [Fact]
    public Task State_Clear_Primitives() =>
        SnapshotStateOperation<string>(
            (codec, writer) => codec.WriteClear(writer),
            ["clear"]);

    [Fact]
    public Task State_Clear_Record() =>
        SnapshotStateOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteClear(writer),
            ["clear"]);

    // --- TaskCompletionSource ------------------------------------------------------------------

    [Fact]
    public Task Tcs_Pending_Primitives() =>
        SnapshotTcsOperation<int>(
            (codec, writer) => codec.WritePending(writer),
            ["pending"]);

    [Fact]
    public Task Tcs_Pending_Record() =>
        SnapshotTcsOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WritePending(writer),
            ["pending"]);

    [Fact]
    public Task Tcs_Completed_Primitives() =>
        SnapshotTcsOperation<int>(
            (codec, writer) => codec.WriteCompleted(99, writer),
            ["completed:99"]);

    [Fact]
    public Task Tcs_Completed_Record() =>
        SnapshotTcsOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteCompleted(SampleRecord, writer),
            [$"completed:{SampleRecord}"]);

    [Fact]
    public Task Tcs_Faulted_Primitives() =>
        SnapshotTcsOperation<int>(
            (codec, writer) => codec.WriteFaulted(MakeStableFault(), writer),
            ["faulted:snapshot-faulted-message"]);

    [Fact]
    public Task Tcs_Faulted_Record() =>
        SnapshotTcsOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteFaulted(MakeStableFault(), writer),
            ["faulted:snapshot-faulted-message"]);

    [Fact]
    public Task Tcs_Canceled_Primitives() =>
        SnapshotTcsOperation<int>(
            (codec, writer) => codec.WriteCanceled(writer),
            ["canceled"]);

    [Fact]
    public Task Tcs_Canceled_Record() =>
        SnapshotTcsOperation<JournalingSnapshotRecord>(
            (codec, writer) => codec.WriteCanceled(writer),
            ["canceled"]);

    // --- Cross-cutting -------------------------------------------------------------------------

    [Fact]
    public Task EmptyBatch()
    {
        // No operations written → committed buffer is the empty UTF-8 string. JSONL has no per-batch
        // header, so this should be byte-empty — pinning that explicitly catches accidental "always emit
        // a BOM/header" regressions in JsonLinesJournalBufferWriter.
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();
        var bytes = SnapshotBytes(writer);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Equal(string.Empty, text);
        return VerifyJsonSnapshot(text);
    }

    [Fact]
    public void Determinism_RepeatedWritesProduceIdenticalText()
    {
        // Utf8JsonWriter is deterministic; this smoke test guards against any future infrastructure
        // change that introduces non-determinism (e.g. timestamping, ID interning) into JSONL output.
        var first = WriteOnce();
        var second = WriteOnce();
        Assert.Equal(first, second);

        string WriteOnce()
        {
            var codec = new JsonDurableDictionaryCommandCodec<string, JournalingSnapshotRecord>(Options);
            var format = new JsonLinesJournalFormat();
            using var writer = format.CreateWriter();
            codec.WriteSet("alpha", SampleRecord, writer.CreateJournalStreamWriter(new JournalStreamId(SnapshotStreamId)));
            return Encoding.UTF8.GetString(SnapshotBytes(writer));
        }
    }

    // --- Internals -----------------------------------------------------------------------------

    /// <summary>
    /// Use a fresh, NEVER-thrown exception so the snapshot never depends on a real stack trace or
    /// captured target site — those vary by runtime, build configuration, and source line numbers.
    /// </summary>
    private static InvalidOperationException MakeStableFault() => new("snapshot-faulted-message");

    private Task SnapshotDictionaryOperation<TKey, TValue>(
        Action<JsonDurableDictionaryCommandCodec<TKey, TValue>, JournalStreamWriter> write,
        string[] expectedCommands)
        where TKey : notnull
    {
        var codec = new JsonDurableDictionaryCommandCodec<TKey, TValue>(Options);
        var state = new RecordingDictionaryState<TKey, TValue>(codec);
        return SnapshotSingleOp(
            codec,
            state,
            writer => write(codec, writer),
            () => Assert.Equal(expectedCommands, state.Commands));
    }

    private Task SnapshotListOperation<T>(
        Action<JsonDurableListCommandCodec<T>, JournalStreamWriter> write,
        string[] expectedCommands)
    {
        var codec = new JsonDurableListCommandCodec<T>(Options);
        var state = new RecordingListState<T>(codec);
        return SnapshotSingleOp(
            codec,
            state,
            writer => write(codec, writer),
            () => Assert.Equal(expectedCommands, state.Commands));
    }

    private Task SnapshotQueueOperation<T>(
        Action<JsonDurableQueueCommandCodec<T>, JournalStreamWriter> write,
        string[] expectedCommands)
    {
        var codec = new JsonDurableQueueCommandCodec<T>(Options);
        var state = new RecordingQueueState<T>(codec);
        return SnapshotSingleOp(
            codec,
            state,
            writer => write(codec, writer),
            () => Assert.Equal(expectedCommands, state.Commands));
    }

    private Task SnapshotSetOperation<T>(
        Action<JsonDurableSetCommandCodec<T>, JournalStreamWriter> write,
        string[] expectedCommands)
    {
        var codec = new JsonDurableSetCommandCodec<T>(Options);
        var state = new RecordingSetState<T>(codec);
        return SnapshotSingleOp(
            codec,
            state,
            writer => write(codec, writer),
            () => Assert.Equal(expectedCommands, state.Commands));
    }

    private Task SnapshotStateOperation<T>(
        Action<JsonPersistentStateCommandCodec<T>, JournalStreamWriter> write,
        string[] expectedCommands)
    {
        var codec = new JsonPersistentStateCommandCodec<T>(Options);
        var state = new RecordingStateState<T>(codec);
        return SnapshotSingleOp(
            codec,
            state,
            writer => write(codec, writer),
            () => Assert.Equal(expectedCommands, state.Commands));
    }

    private Task SnapshotTcsOperation<T>(
        Action<JsonDurableTaskCompletionSourceCommandCodec<T>, JournalStreamWriter> write,
        string[] expectedCommands)
    {
        var codec = new JsonDurableTaskCompletionSourceCommandCodec<T>(Options);
        var state = new RecordingTcsState<T>(codec);
        return SnapshotSingleOp(
            codec,
            state,
            writer => write(codec, writer),
            () => Assert.Equal(expectedCommands, state.Commands));
    }

    private Task SnapshotSingleOp(
        object codec,
        IJournaledState state,
        Action<JournalStreamWriter> write,
        Action assertCommands)
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();
        write(writer.CreateJournalStreamWriter(new JournalStreamId(SnapshotStreamId)));
        var bytes = SnapshotBytes(writer);
        var text = Encoding.UTF8.GetString(bytes);
        AssertNoCarriageReturns(text);
        ReadAndAssert(format, bytes, state, assertCommands);
        return VerifyJsonSnapshot(text);
    }

    private Task SnapshotMultiOp(
        object codec,
        IJournaledState state,
        Action<JournalStreamWriter> writeSequence,
        string[] expectedCommands,
        Func<IReadOnlyList<string>> getActualCommands)
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();
        writeSequence(writer.CreateJournalStreamWriter(new JournalStreamId(SnapshotStreamId)));
        var bytes = SnapshotBytes(writer);
        var text = Encoding.UTF8.GetString(bytes);
        AssertNoCarriageReturns(text);
        ReadAndAssert(format, bytes, state, () => Assert.Equal(expectedCommands, getActualCommands()));
        return VerifyJsonSnapshot(text);
    }

    private static byte[] SnapshotBytes(JournalBufferWriter writer)
    {
        using var slice = writer.GetBuffer();
        return slice.ToArray();
    }

    /// <summary>
    /// Snapshots are committed to git with <c>eol=lf</c> (see <c>.gitattributes</c>); guard against any
    /// future change that introduces <c>\r\n</c> into the writer output, which would make the snapshot
    /// platform-dependent.
    /// </summary>
    private static void AssertNoCarriageReturns(string text) => Assert.DoesNotContain('\r', text);

    private static void ReadAndAssert(JsonLinesJournalFormat format, byte[] bytes, IJournaledState state, Action assertCommands)
    {
        if (bytes.Length == 0)
        {
            assertCommands();
            return;
        }

        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        var buffer = new JournalBufferReader(writer.Reader, isCompleted: true);
        var context = JournalTestReplayContext.Create(JsonJournalExtensions.JournalFormatKey, (new JournalStreamId(SnapshotStreamId), state));
        ((IJournalFormat)format).Replay(buffer, context);
        Assert.Equal(0, buffer.Length);
        assertCommands();
    }

    private static Task VerifyJsonSnapshot(string text) =>
        Verify(text, extension: "jsonl").UseDirectory("snapshots");
}
