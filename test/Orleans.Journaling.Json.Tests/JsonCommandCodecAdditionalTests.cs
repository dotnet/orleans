using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Journaling.Tests;
using Xunit;

namespace Orleans.Journaling.Json.Tests;

[TestCategory("BVT")]
public sealed class JsonCommandCodecAdditionalTests
{
    private static readonly JsonSerializerOptions Options = new() { TypeInfoResolver = JsonCodecTestJsonContext.Default };

    [Fact]
    public void DictionaryCodec_RemoveClearAndEmptySnapshot_RoundTrip()
    {
        var codec = new JsonDurableDictionaryCommandCodec<string, int>(Options);
        var consumer = new RecordingDictionaryCommandHandler<string, int>();

        Apply(codec, writer => codec.WriteRemove("alpha", writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot([], writer), consumer);

        Assert.Equal(["remove:alpha", "clear", "reset:0"], consumer.Commands);
        Assert.Empty(consumer.SnapshotItems);
    }

    [Fact]
    public void DictionaryCodec_Snapshot_UsesConfiguredTypeInfoForValues()
    {
        var codec = new JsonDurableDictionaryCommandCodec<string, JsonCodecTestValue>(Options);
        var consumer = new RecordingDictionaryCommandHandler<string, JsonCodecTestValue>();

        Apply(codec, writer => codec.WriteSnapshot([new("alpha", new("first", 1)), new("beta", new("second", 2))], writer), consumer);

        Assert.Equal(
            [
                new KeyValuePair<string, JsonCodecTestValue>("alpha", new("first", 1)),
                new KeyValuePair<string, JsonCodecTestValue>("beta", new("second", 2))
            ],
            consumer.SnapshotItems);
    }

    [Fact]
    public void CommandCodecs_RejectUnsupportedCommands()
    {
        var payload = """["unknown"]"""u8.ToArray();

        Assert.Throws<NotSupportedException>(() => new JsonDurableDictionaryCommandCodec<string, int>(Options).Apply(CodecTestHelpers.ReadBuffer(payload), new RecordingDictionaryCommandHandler<string, int>()));
        Assert.Throws<NotSupportedException>(() => new JsonDurableListCommandCodec<int>(Options).Apply(CodecTestHelpers.ReadBuffer(payload), new RecordingListCommandHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new JsonDurableQueueCommandCodec<int>(Options).Apply(CodecTestHelpers.ReadBuffer(payload), new RecordingQueueCommandHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new JsonDurableSetCommandCodec<int>(Options).Apply(CodecTestHelpers.ReadBuffer(payload), new RecordingSetCommandHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new JsonDurableValueCommandCodec<int>(Options).Apply(CodecTestHelpers.ReadBuffer(payload), new RecordingValueCommandHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new JsonPersistentStateCommandCodec<int>(Options).Apply(CodecTestHelpers.ReadBuffer(payload), new RecordingPersistentStateCommandHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new JsonDurableTaskCompletionSourceCommandCodec<int>(Options).Apply(CodecTestHelpers.ReadBuffer(payload), new RecordingTaskCompletionSourceCommandHandler<int>()));
    }

    [Fact]
    public void NullablePayloads_RoundTripNull()
    {
        var valueConsumer = new RecordingValueCommandHandler<string>();
        new JsonDurableValueCommandCodec<string>(Options).Apply(
            CodecTestHelpers.ReadBuffer("""["set",null]"""u8.ToArray()),
            valueConsumer);

        Assert.Null(valueConsumer.Value);

        var listConsumer = new RecordingListCommandHandler<string>();
        new JsonDurableListCommandCodec<string>(Options).Apply(
            CodecTestHelpers.ReadBuffer("""["snapshot",["a",null,"c"]]"""u8.ToArray()),
            listConsumer);

        Assert.Equal(["reset:3", "add:a", "add:", "add:c"], listConsumer.Commands);

        var queueConsumer = new RecordingQueueCommandHandler<string>();
        new JsonDurableQueueCommandCodec<string>(Options).Apply(
            CodecTestHelpers.ReadBuffer("""["snapshot",["a",null,"c"]]"""u8.ToArray()),
            queueConsumer);

        Assert.Equal(["reset:3", "enqueue:a", "enqueue:", "enqueue:c"], queueConsumer.Commands);

        var setConsumer = new RecordingSetCommandHandler<string>();
        new JsonDurableSetCommandCodec<string>(Options).Apply(
            CodecTestHelpers.ReadBuffer("""["add",null]"""u8.ToArray()),
            setConsumer);

        Assert.Equal(["add:"], setConsumer.Commands);

        var dictionaryConsumer = new RecordingDictionaryCommandHandler<string, string>();
        new JsonDurableDictionaryCommandCodec<string, string>(Options).Apply(
            CodecTestHelpers.ReadBuffer("""["snapshot",[["key",null]]]"""u8.ToArray()),
            dictionaryConsumer);

        Assert.Collection(
            dictionaryConsumer.SnapshotItems,
            item =>
            {
                Assert.Equal("key", item.Key);
                Assert.Null(item.Value);
            });

        var persistentStateConsumer = new RecordingPersistentStateCommandHandler<string>();
        new JsonPersistentStateCommandCodec<string>(Options).Apply(
            CodecTestHelpers.ReadBuffer("""["set",null,7]"""u8.ToArray()),
            persistentStateConsumer);

        Assert.Null(persistentStateConsumer.State);
        Assert.Equal(7UL, persistentStateConsumer.Version);

        var tcsConsumer = new RecordingTaskCompletionSourceCommandHandler<string>();
        new JsonDurableTaskCompletionSourceCommandCodec<string>(Options).Apply(
            CodecTestHelpers.ReadBuffer("""["completed",null]"""u8.ToArray()),
            tcsConsumer);

        Assert.Equal(["completed:"], tcsConsumer.Commands);
    }

    [Fact]
    public void NonNullableValuePayloads_NullOperand_ThrowsClearException()
    {
        var exception = Assert.Throws<JsonException>(() =>
            new JsonDurableValueCommandCodec<int>(Options).Apply(
                CodecTestHelpers.ReadBuffer("""["set",null]"""u8.ToArray()),
                new RecordingValueCommandHandler<int>()));

        Assert.Contains("must not be null", exception.Message);
        Assert.Contains(JsonJournalEntryFields.Value, exception.Message);
    }

    [Fact]
    public void DictionaryCodec_NullKeyInSnapshotPair_ThrowsClearException()
    {
        var codec = new JsonDurableDictionaryCommandCodec<string, int>(Options);
        var payload = """["snapshot",[[null,1]]]"""u8.ToArray();

        var exception = Assert.Throws<JsonException>(
            () => codec.Apply(CodecTestHelpers.ReadBuffer(payload), new RecordingDictionaryCommandHandler<string, int>()));

        Assert.Contains("must not be null", exception.Message);
        Assert.Contains(JsonJournalEntryFields.Key, exception.Message);
    }

    [Fact]
    public void UseJsonJournalFormat_RegistersEveryCommandCodecByKey()
    {
        var builder = new TestSiloBuilder();

        builder.UseJsonJournalFormat(JsonCodecTestJsonContext.Default);

        using var serviceProvider = builder.Services.BuildServiceProvider();
        Assert.IsType<JsonLinesJournalFormat>(serviceProvider.GetRequiredKeyedService<IJournalFormat>(JsonJournalExtensions.JournalFormatKey));
        Assert.IsType<JsonLinesJournalFormat>(serviceProvider.GetRequiredService<IJournalFormat>());
        CodecTestHelpers.AssertCommandCodecRegistrations(serviceProvider, JsonJournalExtensions.JournalFormatKey);
    }

    [Fact]
    public void KeyedCommandCodecServices_CachePerClosedGenericCodecType()
    {
        var builder = new TestSiloBuilder();
        builder.UseJsonJournalFormat(JsonCodecTestJsonContext.Default);

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var key = JsonJournalExtensions.JournalFormatKey;

        Assert.Same(
            serviceProvider.GetRequiredKeyedService<IDurableDictionaryCommandCodec<string, int>>(key),
            serviceProvider.GetRequiredKeyedService<IDurableDictionaryCommandCodec<string, int>>(key));
        Assert.NotSame(
            serviceProvider.GetRequiredKeyedService<IDurableDictionaryCommandCodec<string, int>>(key),
            serviceProvider.GetRequiredKeyedService<IDurableDictionaryCommandCodec<string, uint>>(key));
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

    private static void Apply<TKey, TValue>(
        IDurableDictionaryCommandCodec<TKey, TValue> codec,
        Action<JournalStreamWriter> write,
        RecordingDictionaryCommandHandler<TKey, TValue> consumer)
        where TKey : notnull
    {
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(write)), consumer);
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
