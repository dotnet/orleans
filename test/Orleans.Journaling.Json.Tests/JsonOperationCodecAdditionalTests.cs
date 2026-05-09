using System.Buffers;
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
        var payload = CodecTestHelpers.Sequence("""["unknown"]"""u8.ToArray());

        Assert.Throws<NotSupportedException>(() => new JsonDictionaryOperationCodec<string, int>(Options).Apply(payload, new RecordingDictionaryOperationHandler<string, int>()));
        Assert.Throws<NotSupportedException>(() => new JsonListOperationCodec<int>(Options).Apply(payload, new RecordingListOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new JsonQueueOperationCodec<int>(Options).Apply(payload, new RecordingQueueOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new JsonSetOperationCodec<int>(Options).Apply(payload, new RecordingSetOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new JsonValueOperationCodec<int>(Options).Apply(payload, new RecordingValueOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new JsonStateOperationCodec<int>(Options).Apply(payload, new RecordingStateOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new JsonTcsOperationCodec<int>(Options).Apply(payload, new RecordingTaskCompletionSourceOperationHandler<int>()));
    }

    [Fact]
    public void UseJsonJournalFormat_RegistersEveryFormatFamilyProviderByKey()
    {
        var builder = new TestSiloBuilder();

        builder.UseJsonJournalFormat(JsonCodecTestJsonContext.Default);

        using var serviceProvider = builder.Services.BuildServiceProvider();
        Assert.IsType<JsonLinesJournalFormat>(serviceProvider.GetRequiredKeyedService<IJournalFormat>(JsonJournalExtensions.JournalFormatKey));
        Assert.IsType<JsonLinesJournalFormat>(serviceProvider.GetRequiredService<IJournalFormat>());
        CodecTestHelpers.AssertCodecProviderRegistrations(
            serviceProvider,
            JsonJournalExtensions.JournalFormatKey,
            serviceProvider.GetRequiredService<JsonOperationCodecProvider>(),
            expectDefaultProvider: true);
    }

    [Fact]
    public void OperationCodecProvider_CachesPerClosedGenericCodecType()
    {
        var provider = new JsonOperationCodecProvider(Options);

        Assert.Same(provider.GetCodec<string, int>(), provider.GetCodec<string, int>());
        Assert.NotSame(provider.GetCodec<string, int>(), provider.GetCodec<string, ulong>());
        Assert.Same(provider.GetCodec<string>(), provider.GetCodec<string>());
        Assert.Same(((IDurableQueueOperationCodecProvider)provider).GetCodec<int>(), ((IDurableQueueOperationCodecProvider)provider).GetCodec<int>());
        Assert.Same(((IDurableSetOperationCodecProvider)provider).GetCodec<int>(), ((IDurableSetOperationCodecProvider)provider).GetCodec<int>());
        Assert.Same(((IDurableValueOperationCodecProvider)provider).GetCodec<int>(), ((IDurableValueOperationCodecProvider)provider).GetCodec<int>());
        Assert.Same(((IDurableStateOperationCodecProvider)provider).GetCodec<int>(), ((IDurableStateOperationCodecProvider)provider).GetCodec<int>());
        Assert.Same(((IDurableTaskCompletionSourceOperationCodecProvider)provider).GetCodec<int>(), ((IDurableTaskCompletionSourceOperationCodecProvider)provider).GetCodec<int>());
    }

    private static void Apply<TKey, TValue>(
        IDurableDictionaryOperationCodec<TKey, TValue> codec,
        Action<JournalStreamWriter> write,
        RecordingDictionaryOperationHandler<TKey, TValue> consumer)
        where TKey : notnull
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
