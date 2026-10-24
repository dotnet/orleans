using System.Buffers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable log journal entries.
/// </summary>
internal sealed class OrleansBinaryDurableLogCommandCodec<T>(
    IFieldCodec<T> codec,
    SerializerSessionPool sessionPool) : IDurableLogCommandCodec<T>
{
    private const uint AppendCommand = 0;
    private const uint TrimBeforeCommand = 1;
    private const uint SnapshotCommand = 2;
    private const uint ClearCommand = 3;
    private const uint SealCommand = 4;
    private const uint EpochCommand = 5;
    private const uint DraftCommitCommand = 6;
    private const uint SealedFlag = 1;

    /// <inheritdoc/>
    public void WriteAppend(ReadOnlySpan<T> items, JournalStreamWriter writer)
    {
        if (items.IsEmpty)
        {
            throw new ArgumentException("An append command requires at least one item.", nameof(items));
        }

        using var entry = writer.BeginEntry();
        var output = entry.Writer;
        var payloadWriter = Writer.Create(output, session: null!);
        payloadWriter.WriteVarUInt32(AppendCommand);
        payloadWriter.WriteVarUInt32((uint)items.Length);
        payloadWriter.Commit();
        foreach (var item in items)
        {
            OrleansBinaryCommandCodecHelpers.WriteValue(codec, item, output, sessionPool);
        }

        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteEpoch(long epoch, JournalStreamWriter writer)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(epoch);
        using var entry = writer.BeginEntry();
        var payloadWriter = Writer.Create(entry.Writer, session: null!);
        payloadWriter.WriteVarUInt32(EpochCommand);
        payloadWriter.WriteVarUInt64((ulong)epoch);
        payloadWriter.Commit();
        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteDraftCommit(LogDraftId draftId, long sequence, ReadOnlySpan<T> items, JournalStreamWriter writer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        using var entry = writer.BeginEntry();
        var output = entry.Writer;
        var payloadWriter = Writer.Create(output, session: null!);
        payloadWriter.WriteVarUInt32(DraftCommitCommand);
        WriteDraftCommit(ref payloadWriter, new(draftId, sequence, items.Length));
        payloadWriter.Commit();
        foreach (var item in items)
        {
            OrleansBinaryCommandCodecHelpers.WriteValue(codec, item, output, sessionPool);
        }

        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteTrimBefore(long sequence, JournalStreamWriter writer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        using var entry = writer.BeginEntry();
        var payloadWriter = Writer.Create(entry.Writer, session: null!);
        payloadWriter.WriteVarUInt32(TrimBeforeCommand);
        payloadWriter.WriteVarUInt64((ulong)sequence);
        payloadWriter.Commit();
        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer) => WriteCommand(ClearCommand, writer);

    /// <inheritdoc/>
    public void WriteSeal(JournalStreamWriter writer) => WriteCommand(SealCommand, writer);

    /// <inheritdoc/>
    public void WriteSnapshot(
        long epoch,
        long firstSequence,
        long resetSequence,
        bool isSealed,
        IReadOnlyCollection<T> items,
        IReadOnlyCollection<LogDraftCommit> draftCommits,
        JournalStreamWriter writer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(epoch);
        ArgumentOutOfRangeException.ThrowIfNegative(resetSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(firstSequence, resetSequence);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(draftCommits);
        using var entry = writer.BeginEntry();
        var output = entry.Writer;
        var count = CollectionCodecHelpers.GetSnapshotCount(items);
        var payloadWriter = Writer.Create(output, session: null!);
        payloadWriter.WriteVarUInt32(SnapshotCommand);
        payloadWriter.WriteVarUInt64((ulong)epoch);
        payloadWriter.WriteVarUInt64((ulong)firstSequence);
        payloadWriter.WriteVarUInt64((ulong)resetSequence);
        payloadWriter.WriteVarUInt32(isSealed ? SealedFlag : 0);
        payloadWriter.WriteVarUInt32((uint)count);
        payloadWriter.Commit();
        var written = 0;
        foreach (var item in items)
        {
            CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
            OrleansBinaryCommandCodecHelpers.WriteValue(codec, item, output, sessionPool);
            written++;
        }

        CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
        var commitCount = CollectionCodecHelpers.GetSnapshotCount(draftCommits);
        payloadWriter = Writer.Create(output, session: null!);
        payloadWriter.WriteVarUInt32((uint)commitCount);
        written = 0;
        foreach (var commit in draftCommits)
        {
            CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(commitCount, written);
            WriteDraftCommit(ref payloadWriter, commit);
            written++;
        }

        CollectionCodecHelpers.RequireSnapshotItemCount(commitCount, written);
        payloadWriter.Commit();
        entry.Commit();
    }

    /// <inheritdoc/>
    public void Apply(JournalBufferReader input, IDurableLogCommandHandler<T> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        using var slice = input.Peek(input.Length);
        using var session = sessionPool.GetSession();
        var reader = Reader.Create(slice, session);
        Apply(ref reader, consumer);
        if (reader.Position != reader.Length)
        {
            throw new InvalidOperationException("Unexpected trailing data after binary journal command.");
        }
    }

    private void Apply<TInput>(ref Reader<TInput> reader, IDurableLogCommandHandler<T> consumer)
    {
        var command = reader.ReadVarUInt32();
        switch (command)
        {
            case AppendCommand:
                var count = OrleansBinaryCollectionWireHelpers.ReadSnapshotCount(ref reader);
                if (count == 0)
                {
                    throw new InvalidOperationException("Malformed binary journal entry: a durable log append command has no items.");
                }

                for (var i = 0; i < count; i++)
                {
                    consumer.ApplyAppend(OrleansBinaryCommandCodecHelpers.ReadValue(codec, ref reader));
                }

                break;
            case TrimBeforeCommand:
                consumer.ApplyTrimBefore(ReadSequence(ref reader));
                break;
            case ClearCommand:
                consumer.ApplyClear();
                break;
            case SealCommand:
                consumer.ApplySeal();
                break;
            case EpochCommand:
                consumer.ApplyEpoch(ReadSequence(ref reader));
                break;
            case DraftCommitCommand:
                var commit = ReadDraftCommit(ref reader);
                for (var i = 0; i < commit.Count; i++)
                {
                    consumer.ApplyAppend(OrleansBinaryCommandCodecHelpers.ReadValue(codec, ref reader));
                }

                consumer.ApplyDraftCommit(commit);
                break;
            case SnapshotCommand:
                ApplySnapshot(ref reader, consumer);
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }

    private void ApplySnapshot<TInput>(ref Reader<TInput> reader, IDurableLogCommandHandler<T> consumer)
    {
        var epoch = ReadSequence(ref reader);
        var firstSequence = ReadSequence(ref reader);
        var resetSequence = ReadSequence(ref reader);
        var flags = reader.ReadVarUInt32();
        if ((flags & ~SealedFlag) != 0)
        {
            throw new InvalidOperationException($"Malformed binary journal entry: unsupported durable log snapshot flags {flags}.");
        }

        var count = OrleansBinaryCollectionWireHelpers.ReadSnapshotCount(ref reader);
        consumer.Reset(epoch, firstSequence, resetSequence, count);
        for (var i = 0; i < count; i++)
        {
            consumer.ApplyAppend(OrleansBinaryCommandCodecHelpers.ReadValue(codec, ref reader));
        }

        var commitCount = OrleansBinaryCollectionWireHelpers.ReadSnapshotCount(ref reader);
        for (var i = 0; i < commitCount; i++)
        {
            consumer.ApplyDraftCommit(ReadDraftCommit(ref reader));
        }

        if ((flags & SealedFlag) != 0)
        {
            consumer.ApplySeal();
        }
    }

    private static void WriteDraftCommit<TBufferWriter>(ref Writer<TBufferWriter> writer, LogDraftCommit commit)
        where TBufferWriter : IBufferWriter<byte>
    {
        GuidCodec.WriteRaw(ref writer, commit.DraftId.InstanceId);
        writer.WriteVarUInt64((ulong)commit.DraftId.Number);
        writer.WriteVarUInt64((ulong)commit.Sequence);
        writer.WriteVarUInt32((uint)commit.Count);
    }

    private static LogDraftCommit ReadDraftCommit<TInput>(ref Reader<TInput> reader)
    {
        var instanceId = GuidCodec.ReadRaw(ref reader);
        var number = ReadSequence(ref reader);
        var sequence = ReadSequence(ref reader);
        var count = OrleansBinaryCollectionWireHelpers.ReadSnapshotCount(ref reader);
        return new(new LogDraftId(instanceId, number), sequence, count);
    }

    private static void WriteCommand(uint command, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var payloadWriter = Writer.Create(entry.Writer, session: null!);
        payloadWriter.WriteVarUInt32(command);
        payloadWriter.Commit();
        entry.Commit();
    }

    private static long ReadSequence<TInput>(ref Reader<TInput> reader)
    {
        var value = reader.ReadVarUInt64();
        if (value > long.MaxValue)
        {
            throw new InvalidOperationException(
                $"Malformed binary journal entry: log sequence {value} exceeds the maximum supported value {long.MaxValue}.");
        }

        return (long)value;
    }
}
