using System.Buffers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Journaling.Json;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class KeyedJournalingRegistrationTests : JournalingTestBase
{
    private const string CustomFormatKey = "custom-test-format";

    [Fact]
    public void AddJournalStorage_RegistersJsonFamilyByDefaultAndBinaryFamilyByFormatKey()
    {
        var builder = new TestSiloBuilder();
        builder.Services.AddSerializer();

        builder.AddJournalStorage();

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var jsonFormat = Assert.IsType<JsonLinesJournalFormat>(serviceProvider.GetRequiredKeyedService<IJournalFormat>(JsonJournalExtensions.JournalFormatKey));
        Assert.Same(jsonFormat, serviceProvider.GetRequiredService<IJournalFormat>());
        Assert.Equal("application/jsonl", jsonFormat.MimeType);
        CodecTestHelpers.AssertOperationCodecServiceRegistrations(serviceProvider, JsonJournalExtensions.JournalFormatKey);

        var binaryFormat = Assert.IsType<OrleansBinaryJournalFormat>(serviceProvider.GetRequiredKeyedService<IJournalFormat>(OrleansBinaryJournalFormat.JournalFormatKey));
        Assert.Equal("application/octet-stream", binaryFormat.MimeType);
        CodecTestHelpers.AssertOperationCodecServiceRegistrations(serviceProvider, OrleansBinaryJournalFormat.JournalFormatKey);
    }

    [Fact]
    public void StateManager_MissingKeyedFormat_ThrowsClearConfigurationError()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var storage = new VolatileJournalStorage();
        var logger = LoggerFactory.CreateLogger<JournaledStateManager>();
        var shared = JournaledStateManagerShared.CreateForTests(
            logger,
            Options.Create(ManagerOptions),
            TimeProvider.System,
            CustomFormatKey);

        var exception = Assert.Throws<InvalidOperationException>(() => new JournaledStateManager(
            storage,
            shared,
            serviceProvider));

        Assert.Contains(CustomFormatKey, exception.Message);
        Assert.Contains(nameof(IJournalFormat), exception.Message);
    }

    [Fact]
    public void DurableService_ResolvesOperationCodecFromJournalFormatKey()
    {
        var storage = new VolatileJournalStorage();
        var wasUsed = false;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IJournalStorage>(_ => storage);
        services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = CustomFormatKey);
        services.AddScoped<JournaledStateManagerShared>();
        services.AddScoped<IStateManager, JournaledStateManager>();
        services.AddKeyedScoped(typeof(IDurableValue<>), KeyedService.AnyKey, typeof(DurableValue<>));
        services.AddKeyedSingleton<IJournalFormat>(CustomFormatKey, new TestJournalFormat());
        services.AddKeyedSingleton<IDurableDictionaryOperationCodec<string, ulong>>(CustomFormatKey, new TestDictionaryCodec<string, ulong>());
        services.AddKeyedSingleton<IDurableDictionaryOperationCodec<string, DateTime>>(CustomFormatKey, new TestDictionaryCodec<string, DateTime>());
        services.AddKeyedSingleton<IDurableValueOperationCodec<int>>(CustomFormatKey, (_, _) =>
        {
            wasUsed = true;
            return new TestValueCodec<int>();
        });

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        _ = scope.ServiceProvider.GetRequiredKeyedService<IDurableValue<int>>("value");

        Assert.True(wasUsed);
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class TestJournalFormat : IJournalFormat
    {
        public string FormatKey => CustomFormatKey;

        public string? MimeType => null;

        public IJournalBatchWriter CreateWriter() => throw new NotSupportedException();

        public void Read(JournalReadBuffer input, IStateResolver resolver) => throw new NotSupportedException();
    }

    private sealed class TestDictionaryCodec<TKey, TValue> : IDurableDictionaryOperationCodec<TKey, TValue>
        where TKey : notnull
    {
        public void WriteSet(TKey key, TValue value, JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteRemove(TKey key, JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteClear(JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, JournalStreamWriter writer) => throw new NotSupportedException();

        public void Apply(ReadOnlySequence<byte> input, IDurableDictionaryOperationHandler<TKey, TValue> consumer) => throw new NotSupportedException();
    }

    private sealed class TestValueCodec<T> : IDurableValueOperationCodec<T>
    {
        public void WriteSet(T value, JournalStreamWriter writer) => throw new NotSupportedException();

        public void Apply(ReadOnlySequence<byte> input, IDurableValueOperationHandler<T> consumer) => throw new NotSupportedException();
    }
}
