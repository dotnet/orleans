using System.Buffers;
using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Journaling.Tests;
using Xunit;

namespace Orleans.Journaling.MessagePack.Tests;

[TestCategory("BVT")]
public sealed class MessagePackOperationCodecAdditionalTests
{
    private static readonly MessagePackSerializerOptions Options = MessagePackSerializerOptions.Standard;

    [Fact]
    public void DictionaryCodec_AllCommands_RoundTrip()
    {
        var codec = new MessagePackDictionaryOperationCodec<string, int>(Options);
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
    }

    [Fact]
    public void OperationCodecs_RejectUnsupportedCommands()
    {
        Assert.Throws<NotSupportedException>(() => new MessagePackDictionaryOperationCodec<string, int>(Options).Apply(CommandOnly(99), new RecordingDictionaryOperationHandler<string, int>()));
        Assert.Throws<NotSupportedException>(() => new MessagePackListOperationCodec<int>(Options).Apply(CommandOnly(99), new RecordingListOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new MessagePackQueueOperationCodec<int>(Options).Apply(CommandOnly(99), new RecordingQueueOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new MessagePackSetOperationCodec<int>(Options).Apply(CommandOnly(99), new RecordingSetOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new MessagePackValueOperationCodec<int>(Options).Apply(CommandAndNil(99), new RecordingValueOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new MessagePackStateOperationCodec<int>(Options).Apply(CommandOnly(99), new RecordingStateOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new MessagePackTcsOperationCodec<int>(Options).Apply(CommandOnly(99), new RecordingTaskCompletionSourceOperationHandler<int>()));
    }

    [Fact]
    public void OperationReader_RejectsMissingCommand()
    {
        var codec = new MessagePackValueOperationCodec<int>(Options);

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(ArrayHeaderOnly(0), new RecordingValueOperationHandler<int>()));

        Assert.Contains("missing command", exception.Message);
    }

    [Fact]
    public void OperationCodecs_RejectOperandCountMismatches()
    {
        var missingValue = Assert.Throws<InvalidOperationException>(
            () => new MessagePackValueOperationCodec<int>(Options).Apply(CommandOnly(0), new RecordingValueOperationHandler<int>()));
        Assert.Contains("expected 2 item(s), found 1", missingValue.Message);

        var extraQueueOperand = Assert.Throws<InvalidOperationException>(
            () => new MessagePackQueueOperationCodec<int>(Options).Apply(CommandAndNil(1), new RecordingQueueOperationHandler<int>()));
        Assert.Contains("expected 1 item(s), found 2", extraQueueOperand.Message);

        var missingVersion = Assert.Throws<InvalidOperationException>(
            () => new MessagePackStateOperationCodec<string>(Options).Apply(CommandAndValue(0, "state"), new RecordingStateOperationHandler<string>()));
        Assert.Contains("expected 3 item(s), found 2", missingVersion.Message);
    }

    [Fact]
    public void ValueCodec_RejectsTrailingData()
    {
        var codec = new MessagePackValueOperationCodec<int>(Options);
        var payload = CodecTestHelpers.WriteEntry(writer => codec.WriteSet(42, writer));
        var buffer = new ArrayBufferWriter<byte>();
        buffer.Write(payload.ToArray());
        var messagePackWriter = new MessagePackWriter(buffer);
        messagePackWriter.WriteNil();
        messagePackWriter.Flush();

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(CodecTestHelpers.Sequence(buffer.WrittenMemory), new RecordingValueOperationHandler<int>()));

        Assert.Contains("trailing data", exception.Message);
    }

    [Fact]
    public void OperationCodecs_RejectTrailingDataAcrossFamilies()
    {
        var listCodec = new MessagePackListOperationCodec<int>(Options);
        AssertTrailingDataRejected(() => listCodec.Apply(
            WithTrailingNil(CodecTestHelpers.WriteEntry(writer => listCodec.WriteRemoveAt(0, writer))),
            new RecordingListOperationHandler<int>()));

        var dictionaryCodec = new MessagePackDictionaryOperationCodec<string, int>(Options);
        AssertTrailingDataRejected(() => dictionaryCodec.Apply(
            WithTrailingNil(CodecTestHelpers.WriteEntry(writer => dictionaryCodec.WriteSet("key", 1, writer))),
            new RecordingDictionaryOperationHandler<string, int>()));

        var setCodec = new MessagePackSetOperationCodec<string>(Options);
        AssertTrailingDataRejected(() => setCodec.Apply(
            WithTrailingNil(CodecTestHelpers.WriteEntry(writer => setCodec.WriteSnapshot(["item"], writer))),
            new RecordingSetOperationHandler<string>()));

        var tcsCodec = new MessagePackTcsOperationCodec<int>(Options);
        AssertTrailingDataRejected(() => tcsCodec.Apply(
            WithTrailingNil(CodecTestHelpers.WriteEntry(writer => tcsCodec.WritePending(writer))),
            new RecordingTaskCompletionSourceOperationHandler<int>()));
    }

    [Fact]
    public void DictionaryCodec_RejectsUnbalancedSnapshotKeyValues()
    {
        var codec = new MessagePackDictionaryOperationCodec<string, int>(Options);
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(5);
        writer.Write(3);
        writer.Write(1);
        writer.Write("alpha");
        writer.Write(1);
        writer.Write("beta");
        writer.Flush();

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(CodecTestHelpers.Sequence(buffer.WrittenMemory), new RecordingDictionaryOperationHandler<string, int>()));

        Assert.Contains("key/value item count", exception.Message);
    }

    [Fact]
    public void OperationCodecs_PreserveMessagePackWirePayloads()
    {
        var valueCodec = new MessagePackValueOperationCodec<int>(Options);
        Assert.Equal(
            [0x92, 0x00, 0x2A],
            CodecTestHelpers.WriteEntry(writer => valueCodec.WriteSet(42, writer)).ToArray());

        var listCodec = new MessagePackListOperationCodec<int>(Options);
        Assert.Equal(
            [0x94, 0x05, 0x02, 0x01, 0x02],
            CodecTestHelpers.WriteEntry(writer => listCodec.WriteSnapshot([1, 2], writer)).ToArray());

        var dictionaryCodec = new MessagePackDictionaryOperationCodec<string, int>(Options);
        Assert.Equal(
            [0x93, 0x00, 0xA1, 0x61, 0x01],
            CodecTestHelpers.WriteEntry(writer => dictionaryCodec.WriteSet("a", 1, writer)).ToArray());
    }

    [Fact]
    public void UseMessagePackJournalingFormat_RegistersEveryFormatFamilyProviderByKey()
    {
        var builder = new TestSiloBuilder();

        builder.UseMessagePackJournalingFormat();

        using var serviceProvider = builder.Services.BuildServiceProvider();
        Assert.IsType<MessagePackLogFormat>(serviceProvider.GetRequiredKeyedService<ILogFormat>(MessagePackJournalingExtensions.LogFormatKey));
        Assert.IsType<MessagePackLogFormat>(serviceProvider.GetRequiredService<ILogFormat>());
        Assert.Same(serviceProvider.GetRequiredService<MessagePackOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableDictionaryOperationCodecProvider>(MessagePackJournalingExtensions.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<MessagePackOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableListOperationCodecProvider>(MessagePackJournalingExtensions.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<MessagePackOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableQueueOperationCodecProvider>(MessagePackJournalingExtensions.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<MessagePackOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableSetOperationCodecProvider>(MessagePackJournalingExtensions.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<MessagePackOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableValueOperationCodecProvider>(MessagePackJournalingExtensions.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<MessagePackOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableStateOperationCodecProvider>(MessagePackJournalingExtensions.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<MessagePackOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableTaskCompletionSourceOperationCodecProvider>(MessagePackJournalingExtensions.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<MessagePackOperationCodecProvider>(), serviceProvider.GetRequiredService<IDurableDictionaryOperationCodecProvider>());
        Assert.Same(serviceProvider.GetRequiredService<MessagePackOperationCodecProvider>(), serviceProvider.GetRequiredService<IDurableListOperationCodecProvider>());
        Assert.Same(serviceProvider.GetRequiredService<MessagePackOperationCodecProvider>(), serviceProvider.GetRequiredService<IDurableQueueOperationCodecProvider>());
        Assert.Same(serviceProvider.GetRequiredService<MessagePackOperationCodecProvider>(), serviceProvider.GetRequiredService<IDurableSetOperationCodecProvider>());
        Assert.Same(serviceProvider.GetRequiredService<MessagePackOperationCodecProvider>(), serviceProvider.GetRequiredService<IDurableValueOperationCodecProvider>());
        Assert.Same(serviceProvider.GetRequiredService<MessagePackOperationCodecProvider>(), serviceProvider.GetRequiredService<IDurableStateOperationCodecProvider>());
        Assert.Same(serviceProvider.GetRequiredService<MessagePackOperationCodecProvider>(), serviceProvider.GetRequiredService<IDurableTaskCompletionSourceOperationCodecProvider>());
    }

    [Fact]
    public void OperationCodecProvider_CachesPerClosedGenericCodecType()
    {
        var provider = new MessagePackOperationCodecProvider(new MessagePackJournalingOptions { SerializerOptions = Options });

        Assert.Same(provider.GetCodec<string, int>(), provider.GetCodec<string, int>());
        Assert.NotSame(provider.GetCodec<string, int>(), provider.GetCodec<string, long>());
        Assert.Same(provider.GetCodec<string>(), provider.GetCodec<string>());
        Assert.Same(((IDurableQueueOperationCodecProvider)provider).GetCodec<int>(), ((IDurableQueueOperationCodecProvider)provider).GetCodec<int>());
        Assert.Same(((IDurableSetOperationCodecProvider)provider).GetCodec<int>(), ((IDurableSetOperationCodecProvider)provider).GetCodec<int>());
        Assert.Same(((IDurableValueOperationCodecProvider)provider).GetCodec<int>(), ((IDurableValueOperationCodecProvider)provider).GetCodec<int>());
        Assert.Same(((IDurableStateOperationCodecProvider)provider).GetCodec<int>(), ((IDurableStateOperationCodecProvider)provider).GetCodec<int>());
        Assert.Same(((IDurableTaskCompletionSourceOperationCodecProvider)provider).GetCodec<int>(), ((IDurableTaskCompletionSourceOperationCodecProvider)provider).GetCodec<int>());
    }

    private static void Apply<TKey, TValue>(
        IDurableDictionaryOperationCodec<TKey, TValue> codec,
        Action<LogStreamWriter> write,
        RecordingDictionaryOperationHandler<TKey, TValue> consumer)
        where TKey : notnull
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private static ReadOnlySequence<byte> CommandOnly(int command)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(1);
        writer.Write(command);
        writer.Flush();
        return CodecTestHelpers.Sequence(buffer.WrittenMemory);
    }

    private static ReadOnlySequence<byte> CommandAndNil(int command)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(2);
        writer.Write(command);
        writer.WriteNil();
        writer.Flush();
        return CodecTestHelpers.Sequence(buffer.WrittenMemory);
    }

    private static ReadOnlySequence<byte> CommandAndValue<T>(int command, T value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(2);
        writer.Write(command);
        MessagePackSerializer.Serialize(ref writer, value, Options);
        writer.Flush();
        return CodecTestHelpers.Sequence(buffer.WrittenMemory);
    }

    private static ReadOnlySequence<byte> ArrayHeaderOnly(int itemCount)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(itemCount);
        writer.Flush();
        return CodecTestHelpers.Sequence(buffer.WrittenMemory);
    }

    private static ReadOnlySequence<byte> WithTrailingNil(ReadOnlySequence<byte> payload)
    {
        var buffer = new ArrayBufferWriter<byte>();
        buffer.Write(payload.ToArray());
        var writer = new MessagePackWriter(buffer);
        writer.WriteNil();
        writer.Flush();
        return CodecTestHelpers.Sequence(buffer.WrittenMemory);
    }

    private static void AssertTrailingDataRejected(Action action)
    {
        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Contains("trailing data", exception.Message);
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
