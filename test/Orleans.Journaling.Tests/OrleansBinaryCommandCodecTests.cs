using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class OrleansBinaryCommandCodecTests : JournalingTestBase
{
    [Fact]
    public void DictionaryCodec_AllCommands_RoundTrip()
    {
        var codec = new OrleansBinaryDurableDictionaryCommandCodec<string, int>(ValueCodec<string>(), ValueCodec<int>(), SessionPool);
        var consumer = new RecordingDictionaryCommandHandler<string, int>();

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
        var codec = new OrleansBinaryDurableListCommandCodec<string>(ValueCodec<string>(), SessionPool);
        var consumer = new RecordingListCommandHandler<string>();

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
        var codec = new OrleansBinaryDurableQueueCommandCodec<int>(ValueCodec<int>(), SessionPool);
        var consumer = new RecordingQueueCommandHandler<int>();

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
        var codec = new OrleansBinaryDurableSetCommandCodec<string>(ValueCodec<string>(), SessionPool);
        var consumer = new RecordingSetCommandHandler<string>();

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
        var valueCodec = new OrleansBinaryDurableValueCommandCodec<string>(ValueCodec<string>(), SessionPool);
        var valueConsumer = new RecordingValueCommandHandler<string>();
        var stateCodec = new OrleansBinaryPersistentStateCommandCodec<string>(ValueCodec<string>(), SessionPool);
        var stateConsumer = new RecordingPersistentStateCommandHandler<string>();
        var tcsCodec = new OrleansBinaryDurableTaskCompletionSourceCommandCodec<int>(ValueCodec<int>(), ValueCodec<Exception>(), SessionPool);
        var tcsConsumer = new RecordingTaskCompletionSourceCommandHandler<int>();

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
    public void Codecs_WriteCommandPayloadWithoutNestedFormatVersion()
    {
        var codec = new OrleansBinaryDurableQueueCommandCodec<int>(ValueCodec<int>(), SessionPool);

        var payload = CodecTestHelpers.WriteEntry(writer => codec.WriteDequeue(writer));

        Assert.Equal(WriteVarUInt32(1), payload);
    }

    [Fact]
    public void Codecs_RejectUnsupportedCommand()
    {
        var codec = new OrleansBinaryDurableQueueCommandCodec<int>(ValueCodec<int>(), SessionPool);

        var exception = Assert.Throws<NotSupportedException>(
            () => codec.Apply(CodecTestHelpers.ReadBuffer(Command(99)), new RecordingQueueCommandHandler<int>()));

        Assert.Contains("Command type 99", exception.Message);
    }

    [Fact]
    public void Codecs_RejectMalformedCommandsAndOperands()
    {
        var valueCodec = new OrleansBinaryDurableValueCommandCodec<byte>(ValueCodec<byte>(), SessionPool);
        Assert.Throws<InvalidOperationException>(
            () => valueCodec.Apply(CodecTestHelpers.ReadBuffer(new byte[] { 0, 0x80 }), new RecordingValueCommandHandler<byte>()));

        Assert.Throws<InvalidOperationException>(
            () => valueCodec.Apply(CodecTestHelpers.ReadBuffer(Command(0)), new RecordingValueCommandHandler<byte>()));

        var listCodec = new OrleansBinaryDurableListCommandCodec<byte>(ValueCodec<byte>(), SessionPool);
        Assert.Throws<InvalidOperationException>(
            () => listCodec.Apply(CodecTestHelpers.ReadBuffer(Command(3)), new RecordingListCommandHandler<byte>()));
    }

    [Fact]
    public void CollectionCodecs_RejectMalformedSnapshotCounts()
    {
        var listCodec = new OrleansBinaryDurableListCommandCodec<byte>(ValueCodec<byte>(), SessionPool);

        var overflow = Assert.Throws<InvalidOperationException>(
            () => listCodec.Apply(CodecTestHelpers.ReadBuffer(Command(5, 0x8000_0000)), new RecordingListCommandHandler<byte>()));
        Assert.Contains("snapshot count", overflow.Message);
        Assert.Contains("exceeds", overflow.Message);

        // Snapshot count must also be capped well below int.MaxValue to prevent corrupted
        // journals from triggering huge in-memory allocations during recovery.
        var overCap = Assert.Throws<InvalidOperationException>(
            () => listCodec.Apply(CodecTestHelpers.ReadBuffer(Command(5, (uint)OrleansBinaryCollectionWireHelpers.MaxSnapshotItemCount + 1)), new RecordingListCommandHandler<byte>()));
        Assert.Contains("snapshot count", overCap.Message);
        Assert.Contains(OrleansBinaryCollectionWireHelpers.MaxSnapshotItemCount.ToString(System.Globalization.CultureInfo.InvariantCulture), overCap.Message);

        Assert.Throws<InvalidOperationException>(
            () => listCodec.Apply(CodecTestHelpers.ReadBuffer(Command(5, 2)), new RecordingListCommandHandler<byte>()));
    }

    [Fact]
    public void DictionaryCodec_RejectsUnbalancedSnapshotKeyValues()
    {
        var codec = new OrleansBinaryDurableDictionaryCommandCodec<byte, byte>(ValueCodec<byte>(), ValueCodec<byte>(), SessionPool);

        Assert.Throws<InvalidOperationException>(
            () => codec.Apply(CodecTestHelpers.ReadBuffer(Command(3, 1)), new RecordingDictionaryCommandHandler<byte, byte>()));

        var extraValue = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(CodecTestHelpers.ReadBuffer(WithTrailingByte(CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot([new(10, 20)], writer)))), new RecordingDictionaryCommandHandler<byte, byte>()));
        Assert.Contains("trailing data", extraValue.Message);
    }

    [Fact]
    public void Codecs_RejectUnexpectedTrailingData()
    {
        var valueCodec = new OrleansBinaryDurableValueCommandCodec<int>(ValueCodec<int>(), SessionPool);
        AssertTrailingDataRejected(() => valueCodec.Apply(
            CodecTestHelpers.ReadBuffer(WithTrailingByte(CodecTestHelpers.WriteEntry(writer => valueCodec.WriteSet(42, writer)))),
            new RecordingValueCommandHandler<int>()));

        var queueCodec = new OrleansBinaryDurableQueueCommandCodec<int>(ValueCodec<int>(), SessionPool);
        AssertTrailingDataRejected(() => queueCodec.Apply(
            CodecTestHelpers.ReadBuffer(WithTrailingByte(CodecTestHelpers.WriteEntry(writer => queueCodec.WriteDequeue(writer)))),
            new RecordingQueueCommandHandler<int>()));

        var listCodec = new OrleansBinaryDurableListCommandCodec<int>(ValueCodec<int>(), SessionPool);
        AssertTrailingDataRejected(() => listCodec.Apply(
            CodecTestHelpers.ReadBuffer(WithTrailingByte(CodecTestHelpers.WriteEntry(writer => listCodec.WriteRemoveAt(0, writer)))),
            new RecordingListCommandHandler<int>()));

        var dictionaryCodec = new OrleansBinaryDurableDictionaryCommandCodec<string, int>(ValueCodec<string>(), ValueCodec<int>(), SessionPool);
        AssertTrailingDataRejected(() => dictionaryCodec.Apply(
            CodecTestHelpers.ReadBuffer(WithTrailingByte(CodecTestHelpers.WriteEntry(writer => dictionaryCodec.WriteSet("key", 1, writer)))),
            new RecordingDictionaryCommandHandler<string, int>()));

        var setCodec = new OrleansBinaryDurableSetCommandCodec<string>(ValueCodec<string>(), SessionPool);
        AssertTrailingDataRejected(() => setCodec.Apply(
            CodecTestHelpers.ReadBuffer(WithTrailingByte(CodecTestHelpers.WriteEntry(writer => setCodec.WriteSnapshot(["item"], writer)))),
            new RecordingSetCommandHandler<string>()));

        var stateCodec = new OrleansBinaryPersistentStateCommandCodec<int>(ValueCodec<int>(), SessionPool);
        AssertTrailingDataRejected(() => stateCodec.Apply(
            CodecTestHelpers.ReadBuffer(WithTrailingByte(CodecTestHelpers.WriteEntry(writer => stateCodec.WriteClear(writer)))),
            new RecordingPersistentStateCommandHandler<int>()));

        var tcsCodec = new OrleansBinaryDurableTaskCompletionSourceCommandCodec<int>(ValueCodec<int>(), ValueCodec<Exception>(), SessionPool);
        AssertTrailingDataRejected(() => tcsCodec.Apply(
            CodecTestHelpers.ReadBuffer(WithTrailingByte(CodecTestHelpers.WriteEntry(writer => tcsCodec.WritePending(writer)))),
            new RecordingTaskCompletionSourceCommandHandler<int>()));
    }

    [Fact]
    public void TcsCodec_RejectsMissingAndUnsupportedStatus()
    {
        var codec = new OrleansBinaryDurableTaskCompletionSourceCommandCodec<int>(ValueCodec<int>(), ValueCodec<Exception>(), SessionPool);

        var missing = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(CodecTestHelpers.ReadBuffer(Array.Empty<byte>()), new RecordingTaskCompletionSourceCommandHandler<int>()));
        var unsupported = Assert.Throws<NotSupportedException>(
            () => codec.Apply(CodecTestHelpers.ReadBuffer(new byte[] { 255 }), new RecordingTaskCompletionSourceCommandHandler<int>()));

        Assert.Contains("status byte", missing.Message);
        Assert.Contains("Unsupported status", unsupported.Message);
    }

    [Fact]
    public void SnapshotWriters_RejectMismatchedCollectionCounts()
    {
        var queueCodec = new OrleansBinaryDurableQueueCommandCodec<int>(ValueCodec<int>(), SessionPool);
        var setCodec = new OrleansBinaryDurableSetCommandCodec<int>(ValueCodec<int>(), SessionPool);

        var queueException = Assert.Throws<InvalidOperationException>(
            () => CodecTestHelpers.WriteEntry(writer => queueCodec.WriteSnapshot(new MiscountedReadOnlyCollection<int>(1, [1, 2]), writer)));
        var setException = Assert.Throws<InvalidOperationException>(
            () => CodecTestHelpers.WriteEntry(writer => setCodec.WriteSnapshot(new MiscountedReadOnlyCollection<int>(1, [1, 2]), writer)));

        Assert.Contains("did not match", queueException.Message);
        Assert.Contains("did not match", setException.Message);
    }

    [Fact]
    public void KeyedCommandCodecServices_CachePerClosedGenericCodecType()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        var key = OrleansBinaryJournalFormat.JournalFormatKey;
        services.AddKeyedSingleton(typeof(IDurableDictionaryCommandCodec<,>), key, typeof(OrleansBinaryDurableDictionaryCommandCodec<,>));
        services.AddKeyedSingleton(typeof(IDurableListCommandCodec<>), key, typeof(OrleansBinaryDurableListCommandCodec<>));
        services.AddKeyedSingleton(typeof(IDurableQueueCommandCodec<>), key, typeof(OrleansBinaryDurableQueueCommandCodec<>));
        services.AddKeyedSingleton(typeof(IDurableSetCommandCodec<>), key, typeof(OrleansBinaryDurableSetCommandCodec<>));
        services.AddKeyedSingleton(typeof(IDurableValueCommandCodec<>), key, typeof(OrleansBinaryDurableValueCommandCodec<>));
        services.AddKeyedSingleton(typeof(IPersistentStateCommandCodec<>), key, typeof(OrleansBinaryPersistentStateCommandCodec<>));
        services.AddKeyedSingleton(typeof(IDurableTaskCompletionSourceCommandCodec<>), key, typeof(OrleansBinaryDurableTaskCompletionSourceCommandCodec<>));

        using var serviceProvider = services.BuildServiceProvider();

        Assert.Same(
            serviceProvider.GetRequiredKeyedService<IDurableDictionaryCommandCodec<string, int>>(key),
            serviceProvider.GetRequiredKeyedService<IDurableDictionaryCommandCodec<string, int>>(key));
        Assert.NotSame(
            serviceProvider.GetRequiredKeyedService<IDurableDictionaryCommandCodec<string, int>>(key),
            serviceProvider.GetRequiredKeyedService<IDurableDictionaryCommandCodec<string, long>>(key));
        Assert.Same(
            serviceProvider.GetRequiredKeyedService<IDurableListCommandCodec<string>>(key),
            serviceProvider.GetRequiredKeyedService<IDurableListCommandCodec<string>>(key));
        Assert.Same(
            serviceProvider.GetRequiredKeyedService<IDurableQueueCommandCodec<int>>(key),
            serviceProvider.GetRequiredKeyedService<IDurableQueueCommandCodec<int>>(key));
        Assert.Same(
            serviceProvider.GetRequiredKeyedService<IDurableSetCommandCodec<int>>(key),
            serviceProvider.GetRequiredKeyedService<IDurableSetCommandCodec<int>>(key));
        Assert.Same(
            serviceProvider.GetRequiredKeyedService<IDurableValueCommandCodec<int>>(key),
            serviceProvider.GetRequiredKeyedService<IDurableValueCommandCodec<int>>(key));
        Assert.Same(
            serviceProvider.GetRequiredKeyedService<IPersistentStateCommandCodec<int>>(key),
            serviceProvider.GetRequiredKeyedService<IPersistentStateCommandCodec<int>>(key));
        Assert.Same(
            serviceProvider.GetRequiredKeyedService<IDurableTaskCompletionSourceCommandCodec<int>>(key),
            serviceProvider.GetRequiredKeyedService<IDurableTaskCompletionSourceCommandCodec<int>>(key));
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

    private IFieldCodec<T> ValueCodec<T>() => CodecProvider.GetCodec<T>();

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
        IDurableDictionaryCommandCodec<TKey, TValue> codec,
        Action<JournalStreamWriter> write,
        RecordingDictionaryCommandHandler<TKey, TValue> consumer)
        where TKey : notnull
    {
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(write)), consumer);
    }

    private static void Apply<T>(
        IDurableListCommandCodec<T> codec,
        Action<JournalStreamWriter> write,
        RecordingListCommandHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(write)), consumer);
    }

    private static void Apply<T>(
        IDurableQueueCommandCodec<T> codec,
        Action<JournalStreamWriter> write,
        RecordingQueueCommandHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(write)), consumer);
    }

    private static void Apply<T>(
        IDurableSetCommandCodec<T> codec,
        Action<JournalStreamWriter> write,
        RecordingSetCommandHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(write)), consumer);
    }

    private static void Apply<T>(
        IDurableValueCommandCodec<T> codec,
        Action<JournalStreamWriter> write,
        RecordingValueCommandHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(write)), consumer);
    }

    private static void Apply<T>(
        IPersistentStateCommandCodec<T> codec,
        Action<JournalStreamWriter> write,
        RecordingPersistentStateCommandHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(write)), consumer);
    }

    private static void Apply<T>(
        IDurableTaskCompletionSourceCommandCodec<T> codec,
        Action<JournalStreamWriter> write,
        RecordingTaskCompletionSourceCommandHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(write)), consumer);
    }

    private static byte[] Command(uint command, params uint[] operands)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = Writer.Create(buffer, session: null!);
        writer.WriteVarUInt32(command);
        foreach (var operand in operands)
        {
            writer.WriteVarUInt32(operand);
        }
        writer.Commit();

        return buffer.WrittenMemory.ToArray();
    }

    private static byte[] AppendBytes(ReadOnlyMemory<byte> payload, params byte[] suffix)
    {
        var bytes = payload.ToArray();
        var originalLength = bytes.Length;
        Array.Resize(ref bytes, originalLength + suffix.Length);
        suffix.CopyTo(bytes.AsSpan(originalLength));
        return bytes;
    }

    private static byte[] WithTrailingByte(ReadOnlyMemory<byte> payload)
    {
        return AppendBytes(payload, 255);
    }

    private static void AssertTrailingDataRejected(Action action)
    {
        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Contains("trailing data", exception.Message);
    }
}
