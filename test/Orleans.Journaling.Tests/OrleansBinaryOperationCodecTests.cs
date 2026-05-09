using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class OrleansBinaryOperationCodecTests : JournalingTestBase
{
    [Fact]
    public void DictionaryCodec_AllCommands_RoundTrip()
    {
        var codec = new OrleansBinaryDictionaryOperationCodec<string, int>(ValueCodec<string>(), ValueCodec<int>());
        var consumer = new RecordingDictionaryOperationHandler<string, int>();

        Apply(codec, writer => codec.WriteSet("alpha", 1, writer), consumer);
        Apply(codec, writer => codec.WriteRemove("alpha", writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot([new("beta", 2), new("gamma", 3)], writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot([], writer), consumer);

        Assert.Equal(
            [
                "set:alpha:1",
                "remove:alpha",
                "clear",
                "reset:2",
                "set:beta:2",
                "set:gamma:3",
                "reset:0"
            ],
            consumer.Commands);
        Assert.Empty(consumer.SnapshotItems);
    }

    [Fact]
    public void ListCodec_AllCommands_RoundTrip()
    {
        var codec = new OrleansBinaryListOperationCodec<string>(ValueCodec<string>());
        var consumer = new RecordingListOperationHandler<string>();

        Apply(codec, writer => codec.WriteAdd("one", writer), consumer);
        Apply(codec, writer => codec.WriteSet(0, "updated", writer), consumer);
        Apply(codec, writer => codec.WriteInsert(1, "two", writer), consumer);
        Apply(codec, writer => codec.WriteRemoveAt(0, writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot(["three", "four"], writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot([], writer), consumer);

        Assert.Equal(
            [
                "add:one",
                "set:0:updated",
                "insert:1:two",
                "remove:0",
                "clear",
                "reset:2",
                "add:three",
                "add:four",
                "reset:0"
            ],
            consumer.Commands);
    }

    [Fact]
    public void QueueCodec_AllCommands_RoundTrip()
    {
        var codec = new OrleansBinaryQueueOperationCodec<int>(ValueCodec<int>());
        var consumer = new RecordingQueueOperationHandler<int>();

        Apply(codec, writer => codec.WriteEnqueue(10, writer), consumer);
        Apply(codec, writer => codec.WriteDequeue(writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot([20, 30], writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot([], writer), consumer);

        Assert.Equal(
            [
                "enqueue:10",
                "dequeue",
                "clear",
                "reset:2",
                "enqueue:20",
                "enqueue:30",
                "reset:0"
            ],
            consumer.Commands);
    }

    [Fact]
    public void SetCodec_AllCommands_RoundTrip()
    {
        var codec = new OrleansBinarySetOperationCodec<string>(ValueCodec<string>());
        var consumer = new RecordingSetOperationHandler<string>();

        Apply(codec, writer => codec.WriteAdd("a", writer), consumer);
        Apply(codec, writer => codec.WriteRemove("a", writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot(["b", "c"], writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot([], writer), consumer);

        Assert.Equal(
            [
                "add:a",
                "remove:a",
                "clear",
                "reset:2",
                "add:b",
                "add:c",
                "reset:0"
            ],
            consumer.Commands);
    }

    [Fact]
    public void ValueStateAndTcsCodecs_AllCommands_RoundTrip()
    {
        var valueCodec = new OrleansBinaryValueOperationCodec<string>(ValueCodec<string>());
        var valueConsumer = new RecordingValueOperationHandler<string>();
        var stateCodec = new OrleansBinaryStateOperationCodec<string>(ValueCodec<string>());
        var stateConsumer = new RecordingStateOperationHandler<string>();
        var tcsCodec = new OrleansBinaryTcsOperationCodec<int>(ValueCodec<int>(), ValueCodec<Exception>());
        var tcsConsumer = new RecordingTaskCompletionSourceOperationHandler<int>();

        Apply(valueCodec, writer => valueCodec.WriteSet("value", writer), valueConsumer);
        Apply(stateCodec, writer => stateCodec.WriteSet("state", 7, writer), stateConsumer);
        Apply(stateCodec, writer => stateCodec.WriteClear(writer), stateConsumer);
        Apply(tcsCodec, writer => tcsCodec.WritePending(writer), tcsConsumer);
        Apply(tcsCodec, writer => tcsCodec.WriteCompleted(5, writer), tcsConsumer);
        Apply(tcsCodec, writer => tcsCodec.WriteFaulted(new InvalidOperationException("boom"), writer), tcsConsumer);
        Apply(tcsCodec, writer => tcsCodec.WriteCanceled(writer), tcsConsumer);

        Assert.Equal("value", valueConsumer.Value);
        Assert.Equal(["set:state:7", "clear"], stateConsumer.Commands);
        Assert.Equal("state", stateConsumer.State);
        Assert.Equal((ulong)7, stateConsumer.Version);
        Assert.Equal(["pending", "completed:5", "faulted:boom", "canceled"], tcsConsumer.Commands);
    }

    [Fact]
    public void Codecs_RejectUnsupportedFormatVersion()
    {
        var codec = new OrleansBinaryValueOperationCodec<int>(ValueCodec<int>());

        var exception = Assert.Throws<NotSupportedException>(
            () => codec.Apply(CodecTestHelpers.Sequence(new byte[] { 1, 0 }), new RecordingValueOperationHandler<int>()));

        Assert.Contains("Unsupported format version", exception.Message);
    }

    [Fact]
    public void Codecs_RejectTruncatedFormatVersion()
    {
        var codec = new OrleansBinaryValueOperationCodec<int>(ValueCodec<int>());

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(CodecTestHelpers.Sequence(Array.Empty<byte>()), new RecordingValueOperationHandler<int>()));

        Assert.Contains("format version", exception.Message);
    }

    [Fact]
    public void Codecs_RejectUnsupportedCommand()
    {
        var codec = new OrleansBinaryQueueOperationCodec<int>(ValueCodec<int>());

        var exception = Assert.Throws<NotSupportedException>(
            () => codec.Apply(VersionedCommand(99), new RecordingQueueOperationHandler<int>()));

        Assert.Contains("Command type 99", exception.Message);
    }

    [Fact]
    public void Codecs_RejectMalformedCommandsAndOperands()
    {
        var valueCodec = new OrleansBinaryValueOperationCodec<byte>(SingleByteValueCodec.Instance);
        Assert.Throws<InvalidOperationException>(
            () => valueCodec.Apply(CodecTestHelpers.Sequence(new byte[] { 0, 0x80 }), new RecordingValueOperationHandler<byte>()));

        var missingValue = Assert.Throws<InvalidOperationException>(
            () => valueCodec.Apply(VersionedCommand(0), new RecordingValueOperationHandler<byte>()));
        Assert.Contains("Missing byte value", missingValue.Message);

        var listCodec = new OrleansBinaryListOperationCodec<byte>(SingleByteValueCodec.Instance);
        Assert.Throws<InvalidOperationException>(
            () => listCodec.Apply(CodecTestHelpers.Sequence(new byte[] { 0, 3 }), new RecordingListOperationHandler<byte>()));
    }

    [Fact]
    public void CollectionCodecs_RejectMalformedSnapshotCounts()
    {
        var listCodec = new OrleansBinaryListOperationCodec<byte>(SingleByteValueCodec.Instance);

        var overflow = Assert.Throws<InvalidOperationException>(
            () => listCodec.Apply(VersionedCommand(5, 0x8000_0000), new RecordingListOperationHandler<byte>()));
        Assert.Contains("snapshot count", overflow.Message);
        Assert.Contains("exceeds", overflow.Message);

        var missingSnapshotItem = Assert.Throws<InvalidOperationException>(
            () => listCodec.Apply(AppendBytes(VersionedCommand(5, 2), 42), new RecordingListOperationHandler<byte>()));
        Assert.Contains("Missing byte value", missingSnapshotItem.Message);
    }

    [Fact]
    public void DictionaryCodec_RejectsUnbalancedSnapshotKeyValues()
    {
        var codec = new OrleansBinaryDictionaryOperationCodec<byte, byte>(SingleByteValueCodec.Instance, SingleByteValueCodec.Instance);

        var missingValue = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(AppendBytes(VersionedCommand(3, 1), 10), new RecordingDictionaryOperationHandler<byte, byte>()));
        Assert.Contains("Missing byte value", missingValue.Message);

        var extraValue = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(AppendBytes(VersionedCommand(3, 1), 10, 20, 30), new RecordingDictionaryOperationHandler<byte, byte>()));
        Assert.Contains("trailing data", extraValue.Message);
    }

    [Fact]
    public void Codecs_RejectUnexpectedTrailingData()
    {
        var valueCodec = new OrleansBinaryValueOperationCodec<int>(ValueCodec<int>());
        AssertTrailingDataRejected(() => valueCodec.Apply(
            WithTrailingByte(CodecTestHelpers.WriteEntry(writer => valueCodec.WriteSet(42, writer))),
            new RecordingValueOperationHandler<int>()));

        var queueCodec = new OrleansBinaryQueueOperationCodec<int>(ValueCodec<int>());
        AssertTrailingDataRejected(() => queueCodec.Apply(
            WithTrailingByte(CodecTestHelpers.WriteEntry(writer => queueCodec.WriteDequeue(writer))),
            new RecordingQueueOperationHandler<int>()));

        var listCodec = new OrleansBinaryListOperationCodec<int>(ValueCodec<int>());
        AssertTrailingDataRejected(() => listCodec.Apply(
            WithTrailingByte(CodecTestHelpers.WriteEntry(writer => listCodec.WriteRemoveAt(0, writer))),
            new RecordingListOperationHandler<int>()));

        var dictionaryCodec = new OrleansBinaryDictionaryOperationCodec<string, int>(ValueCodec<string>(), ValueCodec<int>());
        AssertTrailingDataRejected(() => dictionaryCodec.Apply(
            WithTrailingByte(CodecTestHelpers.WriteEntry(writer => dictionaryCodec.WriteSet("key", 1, writer))),
            new RecordingDictionaryOperationHandler<string, int>()));

        var setCodec = new OrleansBinarySetOperationCodec<string>(ValueCodec<string>());
        AssertTrailingDataRejected(() => setCodec.Apply(
            WithTrailingByte(CodecTestHelpers.WriteEntry(writer => setCodec.WriteSnapshot(["item"], writer))),
            new RecordingSetOperationHandler<string>()));

        var stateCodec = new OrleansBinaryStateOperationCodec<int>(ValueCodec<int>());
        AssertTrailingDataRejected(() => stateCodec.Apply(
            WithTrailingByte(CodecTestHelpers.WriteEntry(writer => stateCodec.WriteClear(writer))),
            new RecordingStateOperationHandler<int>()));

        var tcsCodec = new OrleansBinaryTcsOperationCodec<int>(ValueCodec<int>(), ValueCodec<Exception>());
        AssertTrailingDataRejected(() => tcsCodec.Apply(
            WithTrailingByte(CodecTestHelpers.WriteEntry(writer => tcsCodec.WritePending(writer))),
            new RecordingTaskCompletionSourceOperationHandler<int>()));
    }

    [Fact]
    public void OperationCodecs_PreserveLegacyBinaryWirePayloads()
    {
        var valueCodec = new OrleansBinaryValueOperationCodec<byte>(SingleByteValueCodec.Instance);
        Assert.Equal(
            [0, 1, 42],
            CodecTestHelpers.WriteEntry(writer => valueCodec.WriteSet(42, writer)).ToArray());

        var listCodec = new OrleansBinaryListOperationCodec<byte>(SingleByteValueCodec.Instance);
        Assert.Equal(
            [0, 0x0B, 0x05, 10, 20],
            CodecTestHelpers.WriteEntry(writer => listCodec.WriteSnapshot([10, 20], writer)).ToArray());

        var dictionaryCodec = new OrleansBinaryDictionaryOperationCodec<byte, byte>(SingleByteValueCodec.Instance, SingleByteValueCodec.Instance);
        Assert.Equal(
            [0, 0x07, 0x05, 1, 2, 3, 4],
            CodecTestHelpers.WriteEntry(writer => dictionaryCodec.WriteSnapshot([new(1, 2), new(3, 4)], writer)).ToArray());
    }

    [Fact]
    public void TcsCodec_RejectsMissingAndUnsupportedStatus()
    {
        var codec = new OrleansBinaryTcsOperationCodec<int>(ValueCodec<int>(), ValueCodec<Exception>());

        var missing = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(CodecTestHelpers.Sequence(new byte[] { 0 }), new RecordingTaskCompletionSourceOperationHandler<int>()));
        var unsupported = Assert.Throws<NotSupportedException>(
            () => codec.Apply(CodecTestHelpers.Sequence(new byte[] { 0, 255 }), new RecordingTaskCompletionSourceOperationHandler<int>()));

        Assert.Contains("status byte", missing.Message);
        Assert.Contains("Unsupported status", unsupported.Message);
    }

    [Fact]
    public void SnapshotWriters_RejectMismatchedCollectionCounts()
    {
        var queueCodec = new OrleansBinaryQueueOperationCodec<int>(ValueCodec<int>());
        var setCodec = new OrleansBinarySetOperationCodec<int>(ValueCodec<int>());

        var queueException = Assert.Throws<InvalidOperationException>(
            () => CodecTestHelpers.WriteEntry(writer => queueCodec.WriteSnapshot(new MiscountedReadOnlyCollection<int>(1, [1, 2]), writer)));
        var setException = Assert.Throws<InvalidOperationException>(
            () => CodecTestHelpers.WriteEntry(writer => setCodec.WriteSnapshot(new MiscountedReadOnlyCollection<int>(1, [1, 2]), writer)));

        Assert.Contains("did not match", queueException.Message);
        Assert.Contains("did not match", setException.Message);
    }

    [Fact]
    public void OperationCodecProvider_CachesPerClosedGenericCodecType()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddSingleton(typeof(IJournalValueCodec<>), typeof(OrleansJournalValueCodec<>));
        services.AddSingleton<OrleansBinaryOperationCodecProvider>();

        using var serviceProvider = services.BuildServiceProvider();
        var provider = serviceProvider.GetRequiredService<OrleansBinaryOperationCodecProvider>();

        Assert.Same(provider.GetCodec<string, int>(), provider.GetCodec<string, int>());
        Assert.NotSame(provider.GetCodec<string, int>(), provider.GetCodec<string, long>());
        Assert.Same(provider.GetCodec<string>(), provider.GetCodec<string>());
        Assert.Same(((IDurableQueueOperationCodecProvider)provider).GetCodec<int>(), ((IDurableQueueOperationCodecProvider)provider).GetCodec<int>());
        Assert.Same(((IDurableSetOperationCodecProvider)provider).GetCodec<int>(), ((IDurableSetOperationCodecProvider)provider).GetCodec<int>());
        Assert.Same(((IDurableValueOperationCodecProvider)provider).GetCodec<int>(), ((IDurableValueOperationCodecProvider)provider).GetCodec<int>());
        Assert.Same(((IDurableStateOperationCodecProvider)provider).GetCodec<int>(), ((IDurableStateOperationCodecProvider)provider).GetCodec<int>());
        Assert.Same(((IDurableTaskCompletionSourceOperationCodecProvider)provider).GetCodec<int>(), ((IDurableTaskCompletionSourceOperationCodecProvider)provider).GetCodec<int>());
    }

    [Theory]
    [InlineData(0U)]
    [InlineData(1U)]
    [InlineData(42U)]
    [InlineData(127U)]
    [InlineData(128U)]
    [InlineData(255U)]
    [InlineData(300U)]
    [InlineData(16_383U)]
    [InlineData(16_384U)]
    [InlineData(0x0FFF_FFFFU)]
    [InlineData(0x1000_0000U)]
    [InlineData(uint.MaxValue)]
    public void OrleansSerialization_VarUInt32_RoundTrips(uint value)
    {
        var encoded = WriteVarUInt32(value);
        Assert.Equal(value, ReadSerializerVarUInt32(encoded));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(42UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(255UL)]
    [InlineData(300UL)]
    [InlineData(16_383UL)]
    [InlineData(16_384UL)]
    [InlineData(0x00FF_FFFF_FFFF_FFFFUL)]
    [InlineData(0x0100_0000_0000_0000UL)]
    [InlineData(0x7FFF_FFFF_FFFF_FFFFUL)]
    [InlineData(0x8000_0000_0000_0000UL)]
    [InlineData(ulong.MaxValue)]
    public void OrleansSerialization_VarUInt64_RoundTrips(ulong value)
    {
        var encoded = WriteVarUInt64(value);
        Assert.Equal(value, ReadSerializerVarUInt64(encoded));
    }

    private IJournalValueCodec<T> ValueCodec<T>() => new OrleansJournalValueCodec<T>(CodecProvider.GetCodec<T>(), SessionPool);

    private byte[] WriteVarUInt32(uint value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var session = SessionPool.GetSession();
        var writer = Writer.Create(buffer, session);
        writer.WriteVarUInt32(value);
        writer.Commit();
        return buffer.WrittenMemory.ToArray();
    }

    private byte[] WriteVarUInt64(ulong value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var session = SessionPool.GetSession();
        var writer = Writer.Create(buffer, session);
        writer.WriteVarUInt64(value);
        writer.Commit();
        return buffer.WrittenMemory.ToArray();
    }

    private uint ReadSerializerVarUInt32(byte[] bytes)
    {
        using var session = SessionPool.GetSession();
        var reader = Reader.Create(new ReadOnlySequence<byte>(bytes), session);
        var result = reader.ReadVarUInt32();
        Assert.Equal(bytes.Length, reader.Position);
        return result;
    }

    private ulong ReadSerializerVarUInt64(byte[] bytes)
    {
        using var session = SessionPool.GetSession();
        var reader = Reader.Create(new ReadOnlySequence<byte>(bytes), session);
        var result = reader.ReadVarUInt64();
        Assert.Equal(bytes.Length, reader.Position);
        return result;
    }

    private static void Apply<TKey, TValue>(
        IDurableDictionaryOperationCodec<TKey, TValue> codec,
        Action<JournalStreamWriter> write,
        RecordingDictionaryOperationHandler<TKey, TValue> consumer)
        where TKey : notnull
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private static void Apply<T>(
        IDurableListOperationCodec<T> codec,
        Action<JournalStreamWriter> write,
        RecordingListOperationHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private static void Apply<T>(
        IDurableQueueOperationCodec<T> codec,
        Action<JournalStreamWriter> write,
        RecordingQueueOperationHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private static void Apply<T>(
        IDurableSetOperationCodec<T> codec,
        Action<JournalStreamWriter> write,
        RecordingSetOperationHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private static void Apply<T>(
        IDurableValueOperationCodec<T> codec,
        Action<JournalStreamWriter> write,
        RecordingValueOperationHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private static void Apply<T>(
        IDurableStateOperationCodec<T> codec,
        Action<JournalStreamWriter> write,
        RecordingStateOperationHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private static void Apply<T>(
        IDurableTaskCompletionSourceOperationCodec<T> codec,
        Action<JournalStreamWriter> write,
        RecordingTaskCompletionSourceOperationHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private static ReadOnlySequence<byte> VersionedCommand(uint command, params uint[] operands)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = Writer.Create(buffer, session: null!);
        writer.WriteByte(0);
        writer.WriteVarUInt32(command);
        foreach (var operand in operands)
        {
            writer.WriteVarUInt32(operand);
        }
        writer.Commit();

        return CodecTestHelpers.Sequence(buffer.WrittenMemory);
    }

    private static ReadOnlySequence<byte> AppendBytes(ReadOnlySequence<byte> payload, params byte[] suffix)
    {
        var bytes = payload.ToArray();
        var originalLength = bytes.Length;
        Array.Resize(ref bytes, originalLength + suffix.Length);
        suffix.CopyTo(bytes.AsSpan(originalLength));
        return CodecTestHelpers.Sequence(bytes);
    }

    private static ReadOnlySequence<byte> WithTrailingByte(ReadOnlySequence<byte> payload)
    {
        return AppendBytes(payload, 255);
    }

    private static void AssertTrailingDataRejected(Action action)
    {
        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Contains("trailing data", exception.Message);
    }

    private sealed class SingleByteValueCodec : IJournalValueCodec<byte>
    {
        public static SingleByteValueCodec Instance { get; } = new();

        public void Write(byte value, IBufferWriter<byte> output)
        {
            var span = output.GetSpan(1);
            span[0] = value;
            output.Advance(1);
        }

        public byte Read(ReadOnlySequence<byte> input, out long bytesConsumed)
        {
            var reader = new SequenceReader<byte>(input);
            if (!reader.TryRead(out var value))
            {
                throw new InvalidOperationException("Missing byte value.");
            }

            bytesConsumed = reader.Consumed;
            return value;
        }
    }
}
