using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Journaling.Tests;
using Xunit;

namespace Orleans.Journaling.Json.Tests;

[TestCategory("BVT")]
public sealed class JsonOperationCodecAdditionalTests
{
    private static readonly JsonSerializerOptions Options = new() { TypeInfoResolver = JsonCodecTestJsonContext.Default };

    [Fact]
    public void DictionaryCodec_RemoveClearAndEmptySnapshot_RoundTrip()
    {
        var codec = new JsonDictionaryOperationCodec<string, int>(Options);
        var consumer = new RecordingDictionaryOperationHandler<string, int>();

        Apply(codec, writer => codec.WriteRemove("alpha", writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot([], writer), consumer);

        Assert.Equal(["remove:alpha", "clear", "reset:0"], consumer.Commands);
        Assert.Empty(consumer.SnapshotItems);
    }

    [Fact]
    public void DictionaryCodec_Snapshot_UsesConfiguredTypeInfoForValues()
    {
        var codec = new JsonDictionaryOperationCodec<string, JsonCodecTestValue>(Options);
        var consumer = new RecordingDictionaryOperationHandler<string, JsonCodecTestValue>();

        Apply(codec, writer => codec.WriteSnapshot([new("alpha", new("first", 1)), new("beta", new("second", 2))], writer), consumer);

        Assert.Equal(
            [
                new KeyValuePair<string, JsonCodecTestValue>("alpha", new("first", 1)),
                new KeyValuePair<string, JsonCodecTestValue>("beta", new("second", 2))
            ],
            consumer.SnapshotItems);
    }

    [Fact]
    public void OperationCodecs_RejectUnsupportedCommands()
    {
        var payload = """["unknown"]"""u8.ToArray();

        Assert.Throws<NotSupportedException>(() => new JsonDictionaryOperationCodec<string, int>(Options).Apply(CodecTestHelpers.ReadBuffer(payload), new RecordingDictionaryOperationHandler<string, int>()));
        Assert.Throws<NotSupportedException>(() => new JsonListOperationCodec<int>(Options).Apply(CodecTestHelpers.ReadBuffer(payload), new RecordingListOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new JsonQueueOperationCodec<int>(Options).Apply(CodecTestHelpers.ReadBuffer(payload), new RecordingQueueOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new JsonSetOperationCodec<int>(Options).Apply(CodecTestHelpers.ReadBuffer(payload), new RecordingSetOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new JsonValueOperationCodec<int>(Options).Apply(CodecTestHelpers.ReadBuffer(payload), new RecordingValueOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new JsonStateOperationCodec<int>(Options).Apply(CodecTestHelpers.ReadBuffer(payload), new RecordingStateOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new JsonTcsOperationCodec<int>(Options).Apply(CodecTestHelpers.ReadBuffer(payload), new RecordingTaskCompletionSourceOperationHandler<int>()));
    }

    [Fact]
    public void ValueCodec_NullOperand_ThrowsClearException()
    {
        var codec = new JsonValueOperationCodec<string>(Options);
        var payload = """["set",null]"""u8.ToArray();

        var exception = Assert.Throws<JsonException>(
            () => codec.Apply(CodecTestHelpers.ReadBuffer(payload), new RecordingValueOperationHandler<string>()));

        Assert.Contains("must not be null", exception.Message);
        Assert.Contains(JsonJournalEntryFields.Value, exception.Message);
    }

    [Fact]
    public void ListCodec_NullSnapshotItem_ThrowsClearException()
    {
        var codec = new JsonListOperationCodec<string>(Options);
        var payload = """["snapshot",["a",null,"c"]]"""u8.ToArray();

        var exception = Assert.Throws<JsonException>(
            () => codec.Apply(CodecTestHelpers.ReadBuffer(payload), new RecordingListOperationHandler<string>()));

        Assert.Contains("must not be null", exception.Message);
        Assert.Contains(JsonJournalEntryFields.Item, exception.Message);
    }

    [Fact]
    public void DictionaryCodec_NullKeyInSnapshotPair_ThrowsClearException()
    {
        var codec = new JsonDictionaryOperationCodec<string, int>(Options);
        var payload = """["snapshot",[[null,1]]]"""u8.ToArray();

        var exception = Assert.Throws<JsonException>(
            () => codec.Apply(CodecTestHelpers.ReadBuffer(payload), new RecordingDictionaryOperationHandler<string, int>()));

        Assert.Contains("must not be null", exception.Message);
        Assert.Contains(JsonJournalEntryFields.Key, exception.Message);
    }

    [Fact]
    public void UseJsonJournalFormat_RegistersEveryOperationCodecByKey()
    {
        var builder = new TestSiloBuilder();

        builder.UseJsonJournalFormat(JsonCodecTestJsonContext.Default);

        using var serviceProvider = builder.Services.BuildServiceProvider();
        Assert.IsType<JsonLinesJournalFormat>(serviceProvider.GetRequiredKeyedService<IJournalFormat>(JsonJournalExtensions.JournalFormatKey));
        Assert.IsType<JsonLinesJournalFormat>(serviceProvider.GetRequiredService<IJournalFormat>());
        CodecTestHelpers.AssertOperationCodecRegistrations(serviceProvider, JsonJournalExtensions.JournalFormatKey);
    }

    [Fact]
    public void KeyedOperationCodecServices_CachePerClosedGenericCodecType()
    {
        var builder = new TestSiloBuilder();
        builder.UseJsonJournalFormat(JsonCodecTestJsonContext.Default);

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var key = JsonJournalExtensions.JournalFormatKey;

        Assert.Same(
            serviceProvider.GetRequiredKeyedService<IDictionaryOperationCodec<string, int>>(key),
            serviceProvider.GetRequiredKeyedService<IDictionaryOperationCodec<string, int>>(key));
        Assert.NotSame(
            serviceProvider.GetRequiredKeyedService<IDictionaryOperationCodec<string, int>>(key),
            serviceProvider.GetRequiredKeyedService<IDictionaryOperationCodec<string, ulong>>(key));
        Assert.Same(
            serviceProvider.GetRequiredKeyedService<IListOperationCodec<string>>(key),
            serviceProvider.GetRequiredKeyedService<IListOperationCodec<string>>(key));
        Assert.Same(
            serviceProvider.GetRequiredKeyedService<IQueueOperationCodec<int>>(key),
            serviceProvider.GetRequiredKeyedService<IQueueOperationCodec<int>>(key));
        Assert.Same(
            serviceProvider.GetRequiredKeyedService<ISetOperationCodec<int>>(key),
            serviceProvider.GetRequiredKeyedService<ISetOperationCodec<int>>(key));
        Assert.Same(
            serviceProvider.GetRequiredKeyedService<IValueOperationCodec<int>>(key),
            serviceProvider.GetRequiredKeyedService<IValueOperationCodec<int>>(key));
        Assert.Same(
            serviceProvider.GetRequiredKeyedService<IStateOperationCodec<int>>(key),
            serviceProvider.GetRequiredKeyedService<IStateOperationCodec<int>>(key));
        Assert.Same(
            serviceProvider.GetRequiredKeyedService<ITaskCompletionSourceOperationCodec<int>>(key),
            serviceProvider.GetRequiredKeyedService<ITaskCompletionSourceOperationCodec<int>>(key));
    }

    private static void Apply<TKey, TValue>(
        IDictionaryOperationCodec<TKey, TValue> codec,
        Action<JournalStreamWriter> write,
        RecordingDictionaryOperationHandler<TKey, TValue> consumer)
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
