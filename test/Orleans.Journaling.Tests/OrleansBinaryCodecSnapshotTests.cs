using System.Buffers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Codecs;
using Xunit;
using static VerifyXunit.Verifier;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Snapshot tests pinning the OrleansBinary on-disk wire format produced by the
/// <c>OrleansBinary*CommandCodec</c> implementations under <see cref="Orleans.Journaling"/>.
/// </summary>
/// <remarks>
/// <para>
/// These tests guard against accidental OrleansBinary wire-format changes. Every command writes its
/// operands through the Orleans <c>IFieldCodec</c> path, matching the legacy value encoding while the
/// entry framing is pinned by the snapshot bytes.
/// </para>
/// <para>
/// Since this branch flips the default journal format from OrleansBinary to JSONL, the tests below
/// explicitly construct <see cref="OrleansBinaryJournalFormat"/>/<see cref="OrleansBinaryJournalBufferWriter"/>
/// to opt into the binary path. Each scenario:
/// 1. writes a single operation through a real codec into a binary batch,
/// 2. round-trips the produced bytes through <see cref="OrleansBinaryJournalFormat"/> against a recording
///    state to prove the bytes still parse and apply, then
/// 3. snapshots the human-readable disassembly via Verify.Xunit (file
///    <c>snapshots/&lt;TestName&gt;.verified.txt</c>). The first line of every snapshot is the byte-level
///    HEX baseline — any wire-format drift fails on that single line.
/// </para>
/// </remarks>
[TestCategory("BVT")]
public sealed class OrleansBinaryCodecSnapshotTests : JournalingTestBase
{
    private const uint SnapshotStreamId = 8;

    private static readonly JournalingSnapshotRecord SampleRecord = new()
    {
        Name = "snapshot-record",
        Count = 7,
        Tag = new JournalingSnapshotRecordTag { Label = "tag", Code = 99 },
    };

    private static readonly JournalingSnapshotRecord SampleRecordAlt = new()
    {
        Name = "alt-record",
        Count = 13,
        Tag = new JournalingSnapshotRecordTag { Label = "alt-tag", Code = 17 },
    };

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
        var codec = new OrleansBinaryDurableDictionaryCommandCodec<string, int>(ValueCodec<string>(), ValueCodec<int>(), SessionPool);
        return SnapshotMultiOp(
            codec,
            new RecordingDictionaryState<string, int>(codec),
            writer =>
            {
                codec.WriteSet("alpha", 1, writer);
                codec.WriteSet("beta", 2, writer);
                codec.WriteRemove("alpha", writer);
                codec.WriteSet("gamma", 3, writer);
            },
            ["set:alpha:1", "set:beta:2", "remove:alpha", "set:gamma:3"]);
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
        var codec = new OrleansBinaryDurableListCommandCodec<string>(ValueCodec<string>(), SessionPool);
        return SnapshotMultiOp(
            codec,
            new RecordingListState<string>(codec),
            writer =>
            {
                codec.WriteAdd("one", writer);
                codec.WriteAdd("two", writer);
                codec.WriteSet(1, "updated", writer);
                codec.WriteInsert(0, "head", writer);
                codec.WriteRemoveAt(2, writer);
                codec.WriteSnapshot(["alpha", "beta"], writer);
            },
            ["add:one", "add:two", "set:1:updated", "insert:0:head", "remove:2", "reset:2", "add:alpha", "add:beta"]);
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
        var codec = new OrleansBinaryDurableQueueCommandCodec<int>(ValueCodec<int>(), SessionPool);
        return SnapshotMultiOp(
            codec,
            new RecordingQueueState<int>(codec),
            writer =>
            {
                codec.WriteEnqueue(10, writer);
                codec.WriteEnqueue(20, writer);
                codec.WriteDequeue(writer);
                codec.WriteSnapshot([30, 40], writer);
            },
            ["enqueue:10", "enqueue:20", "dequeue", "reset:2", "enqueue:30", "enqueue:40"]);
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
        var codec = new OrleansBinaryDurableSetCommandCodec<string>(ValueCodec<string>(), SessionPool);
        return SnapshotMultiOp(
            codec,
            new RecordingSetState<string>(codec),
            writer =>
            {
                codec.WriteAdd("alpha", writer);
                codec.WriteAdd("beta", writer);
                codec.WriteRemove("alpha", writer);
                codec.WriteSnapshot(["beta", "gamma"], writer);
            },
            ["add:alpha", "add:beta", "remove:alpha", "reset:2", "add:beta", "add:gamma"]);
    }

    // --- Value ---------------------------------------------------------------------------------

    [Fact]
    public Task Value_Set_Primitives()
    {
        var codec = new OrleansBinaryDurableValueCommandCodec<int>(ValueCodec<int>(), SessionPool);
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
        var codec = new OrleansBinaryDurableValueCommandCodec<JournalingSnapshotRecord>(ValueCodec<JournalingSnapshotRecord>(), SessionPool);
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
        // No operations written → committed buffer is the empty byte sequence. Pinning the empty-batch
        // shape catches accidental "always emit a header" regressions in OrleansBinaryJournalBufferWriter.
        using var batch = new OrleansBinaryJournalBufferWriter();
        var bytes = SnapshotBytes(batch);
        Assert.Empty(bytes);
        return VerifyBinarySnapshot(bytes);
    }

    [Fact]
    public void Determinism_RepeatedWritesProduceIdenticalBytes()
    {
        // Single-operand writes rent a fresh SerializerSessionPool session per operand call, so they
        // should be byte-identical across runs. Explicit smoke check guards
        // against accidental session-state leakage if that contract changes.
        var first = WriteOnce();
        var second = WriteOnce();
        Assert.Equal(JournalSnapshotFormatting.HexBaseline(first), JournalSnapshotFormatting.HexBaseline(second));

        byte[] WriteOnce()
        {
            var codec = new OrleansBinaryDurableDictionaryCommandCodec<string, JournalingSnapshotRecord>(
                ValueCodec<string>(), ValueCodec<JournalingSnapshotRecord>(), SessionPool);
            using var batch = new OrleansBinaryJournalBufferWriter();
            codec.WriteSet("alpha", SampleRecord, batch.CreateJournalStreamWriter(new JournalStreamId(SnapshotStreamId)));
            return SnapshotBytes(batch);
        }
    }

    // --- Internals -----------------------------------------------------------------------------

    private IFieldCodec<T> ValueCodec<T>() => CodecProvider.GetCodec<T>();

    /// <summary>
    /// Use a fresh, NEVER-thrown exception so the snapshot never depends on a real stack trace or
    /// target-site captured by <see cref="Exception"/> when it is thrown — those vary by runtime,
    /// build configuration, and source line numbers.
    /// </summary>
    private static InvalidOperationException MakeStableFault() => new("snapshot-faulted-message");

    private Task SnapshotDictionaryOperation<TKey, TValue>(
        Action<OrleansBinaryDurableDictionaryCommandCodec<TKey, TValue>, JournalStreamWriter> write,
        string[] expectedCommands)
        where TKey : notnull
    {
        var keyCodec = ValueCodec<TKey>();
        var valueCodec = ValueCodec<TValue>();
        var codec = new OrleansBinaryDurableDictionaryCommandCodec<TKey, TValue>(keyCodec, valueCodec, SessionPool);
        var state = new RecordingDictionaryState<TKey, TValue>(codec);
        return SnapshotSingleOp(
            codec,
            state,
            writer => write(codec, writer),
            () => Assert.Equal(expectedCommands, state.Commands));
    }

    private Task SnapshotListOperation<T>(
        Action<OrleansBinaryDurableListCommandCodec<T>, JournalStreamWriter> write,
        string[] expectedCommands)
    {
        var elementCodec = ValueCodec<T>();
        var codec = new OrleansBinaryDurableListCommandCodec<T>(elementCodec, SessionPool);
        var state = new RecordingListState<T>(codec);
        return SnapshotSingleOp(
            codec,
            state,
            writer => write(codec, writer),
            () => Assert.Equal(expectedCommands, state.Commands));
    }

    private Task SnapshotQueueOperation<T>(
        Action<OrleansBinaryDurableQueueCommandCodec<T>, JournalStreamWriter> write,
        string[] expectedCommands)
    {
        var elementCodec = ValueCodec<T>();
        var codec = new OrleansBinaryDurableQueueCommandCodec<T>(elementCodec, SessionPool);
        var state = new RecordingQueueState<T>(codec);
        return SnapshotSingleOp(
            codec,
            state,
            writer => write(codec, writer),
            () => Assert.Equal(expectedCommands, state.Commands));
    }

    private Task SnapshotSetOperation<T>(
        Action<OrleansBinaryDurableSetCommandCodec<T>, JournalStreamWriter> write,
        string[] expectedCommands)
    {
        var elementCodec = ValueCodec<T>();
        var codec = new OrleansBinaryDurableSetCommandCodec<T>(elementCodec, SessionPool);
        var state = new RecordingSetState<T>(codec);
        return SnapshotSingleOp(
            codec,
            state,
            writer => write(codec, writer),
            () => Assert.Equal(expectedCommands, state.Commands));
    }

    private Task SnapshotStateOperation<T>(
        Action<OrleansBinaryPersistentStateCommandCodec<T>, JournalStreamWriter> write,
        string[] expectedCommands)
    {
        var elementCodec = ValueCodec<T>();
        var codec = new OrleansBinaryPersistentStateCommandCodec<T>(elementCodec, SessionPool);
        var state = new RecordingStateState<T>(codec);
        return SnapshotSingleOp(
            codec,
            state,
            writer => write(codec, writer),
            () => Assert.Equal(expectedCommands, state.Commands));
    }

    private Task SnapshotTcsOperation<T>(
        Action<OrleansBinaryDurableTaskCompletionSourceCommandCodec<T>, JournalStreamWriter> write,
        string[] expectedCommands)
    {
        var valueCodec = ValueCodec<T>();
        var exceptionCodec = ValueCodec<Exception>();
        var codec = new OrleansBinaryDurableTaskCompletionSourceCommandCodec<T>(valueCodec, exceptionCodec, SessionPool);
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
        using var batch = new OrleansBinaryJournalBufferWriter();
        write(batch.CreateJournalStreamWriter(new JournalStreamId(SnapshotStreamId)));
        var bytes = SnapshotBytes(batch);
        ReadAndAssert(bytes, state, assertCommands);
        return VerifyBinarySnapshot(bytes);
    }

    private Task SnapshotMultiOp(
        object codec,
        IJournaledState state,
        Action<JournalStreamWriter> writeSequence,
        string[] expectedCommands)
    {
        using var batch = new OrleansBinaryJournalBufferWriter();
        writeSequence(batch.CreateJournalStreamWriter(new JournalStreamId(SnapshotStreamId)));
        var bytes = SnapshotBytes(batch);
        ReadAndAssert(bytes, state, () => AssertCommandsEqual(expectedCommands, state));
        return VerifyBinarySnapshot(bytes);
    }

    private static void AssertCommandsEqual(string[] expected, IJournaledState state)
    {
        var commands = state switch
        {
            RecordingDictionaryState<string, int> d => d.Commands,
            RecordingListState<string> l => l.Commands,
            RecordingQueueState<int> q => q.Commands,
            RecordingSetState<string> s => s.Commands,
            _ => throw new InvalidOperationException($"Unhandled state type {state.GetType().FullName}"),
        };

        Assert.Equal(expected, commands);
    }

    private static byte[] SnapshotBytes(OrleansBinaryJournalBufferWriter batch)
    {
        using var slice = batch.GetBuffer();
        return slice.ToArray();
    }

    private void ReadAndAssert(byte[] bytes, IJournaledState state, Action assertCommands)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        var buffer = new JournalBufferReader(writer.Reader, isCompleted: true);
        var context = JournalTestReplayContext.Create(OrleansBinaryJournalFormat.JournalFormatKey, (new JournalStreamId(SnapshotStreamId), state));
        ((IJournalFormat)new OrleansBinaryJournalFormat(SessionPool)).Replay(buffer, context);
        Assert.Equal(0, buffer.Length);
        assertCommands();
    }

    private static Task VerifyBinarySnapshot(byte[] bytes) =>
        Verify(JournalSnapshotFormatting.FormatBinary(bytes), extension: "txt").UseDirectory("snapshots");
}
