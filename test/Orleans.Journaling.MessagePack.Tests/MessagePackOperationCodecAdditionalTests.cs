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

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
