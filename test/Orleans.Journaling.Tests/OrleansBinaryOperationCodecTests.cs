using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
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
                "snapshot:2",
                "snapshot-item:beta:2",
                "snapshot-item:gamma:3",
                "snapshot:0"
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
                "snapshot:2",
                "snapshot-item:three",
                "snapshot-item:four",
                "snapshot:0"
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
                "snapshot:2",
                "snapshot-item:20",
                "snapshot-item:30",
                "snapshot:0"
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
                "snapshot:2",
                "snapshot-item:b",
                "snapshot-item:c",
                "snapshot:0"
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
    public void Codecs_RejectUnsupportedCommand()
    {
        var codec = new OrleansBinaryQueueOperationCodec<int>(ValueCodec<int>());

        var exception = Assert.Throws<NotSupportedException>(
            () => codec.Apply(VersionedCommand(99), new RecordingQueueOperationHandler<int>()));

        Assert.Contains("Command type 99", exception.Message);
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
            () => queueCodec.WriteSnapshot(new MiscountedReadOnlyCollection<int>(1, [1, 2]), new ArrayBufferWriter<byte>()));
        var setException = Assert.Throws<InvalidOperationException>(
            () => setCodec.WriteSnapshot(new MiscountedReadOnlyCollection<int>(1, [1, 2]), new ArrayBufferWriter<byte>()));

        Assert.Contains("did not match", queueException.Message);
        Assert.Contains("did not match", setException.Message);
    }

    [Fact]
    public void OperationCodecProvider_CachesPerClosedGenericCodecType()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddSingleton(typeof(ILogValueCodec<>), typeof(OrleansLogValueCodec<>));
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

    private ILogValueCodec<T> ValueCodec<T>() => new OrleansLogValueCodec<T>(CodecProvider.GetCodec<T>(), SessionPool);

    private static void Apply<TKey, TValue>(
        IDurableDictionaryOperationCodec<TKey, TValue> codec,
        Action<IBufferWriter<byte>> write,
        RecordingDictionaryOperationHandler<TKey, TValue> consumer)
        where TKey : notnull
    {
        var buffer = CodecTestHelpers.Write(write);
        codec.Apply(CodecTestHelpers.Sequence(buffer.WrittenMemory), consumer);
    }

    private static void Apply<T>(
        IDurableListOperationCodec<T> codec,
        Action<IBufferWriter<byte>> write,
        RecordingListOperationHandler<T> consumer)
    {
        var buffer = CodecTestHelpers.Write(write);
        codec.Apply(CodecTestHelpers.Sequence(buffer.WrittenMemory), consumer);
    }

    private static void Apply<T>(
        IDurableQueueOperationCodec<T> codec,
        Action<IBufferWriter<byte>> write,
        RecordingQueueOperationHandler<T> consumer)
    {
        var buffer = CodecTestHelpers.Write(write);
        codec.Apply(CodecTestHelpers.Sequence(buffer.WrittenMemory), consumer);
    }

    private static void Apply<T>(
        IDurableSetOperationCodec<T> codec,
        Action<IBufferWriter<byte>> write,
        RecordingSetOperationHandler<T> consumer)
    {
        var buffer = CodecTestHelpers.Write(write);
        codec.Apply(CodecTestHelpers.Sequence(buffer.WrittenMemory), consumer);
    }

    private static void Apply<T>(
        IDurableValueOperationCodec<T> codec,
        Action<IBufferWriter<byte>> write,
        RecordingValueOperationHandler<T> consumer)
    {
        var buffer = CodecTestHelpers.Write(write);
        codec.Apply(CodecTestHelpers.Sequence(buffer.WrittenMemory), consumer);
    }

    private static void Apply<T>(
        IDurableStateOperationCodec<T> codec,
        Action<IBufferWriter<byte>> write,
        RecordingStateOperationHandler<T> consumer)
    {
        var buffer = CodecTestHelpers.Write(write);
        codec.Apply(CodecTestHelpers.Sequence(buffer.WrittenMemory), consumer);
    }

    private static void Apply<T>(
        IDurableTaskCompletionSourceOperationCodec<T> codec,
        Action<IBufferWriter<byte>> write,
        RecordingTaskCompletionSourceOperationHandler<T> consumer)
    {
        var buffer = CodecTestHelpers.Write(write);
        codec.Apply(CodecTestHelpers.Sequence(buffer.WrittenMemory), consumer);
    }

    private static ReadOnlySequence<byte> VersionedCommand(uint command)
    {
        var buffer = new ArrayBufferWriter<byte>();
        buffer.GetSpan(1)[0] = 0;
        buffer.Advance(1);
        VarIntHelper.WriteVarUInt32(buffer, command);
        return CodecTestHelpers.Sequence(buffer.WrittenMemory);
    }
}
