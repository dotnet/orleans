using System.Buffers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Journaling.Json;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
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
        CodecTestHelpers.AssertCommandCodecServiceRegistrations(serviceProvider, JsonJournalExtensions.JournalFormatKey);

        var binaryFormat = Assert.IsType<OrleansBinaryJournalFormat>(serviceProvider.GetRequiredKeyedService<IJournalFormat>(OrleansBinaryJournalFormat.JournalFormatKey));
        Assert.Equal("application/octet-stream", binaryFormat.MimeType);
        CodecTestHelpers.AssertCommandCodecServiceRegistrations(serviceProvider, OrleansBinaryJournalFormat.JournalFormatKey);
    }

    [Fact]
    public void StateManager_MissingKeyedFormat_ThrowsClearConfigurationError()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var storage = new VolatileJournalStorage();
        var logger = LoggerFactory.CreateLogger<JournaledStateManager>();
        var options = new JournaledStateManagerOptions
        {
            JournalFormatKey = CustomFormatKey,
            RetirementGracePeriod = ManagerOptions.RetirementGracePeriod
        };
        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            var shared = new JournaledStateManagerShared(
                logger,
                Options.Create(options),
                TimeProvider.System,
                serviceProvider);

            _ = new JournaledStateManager(shared, storage);
        });

        Assert.Contains(CustomFormatKey, exception.Message);
        Assert.Contains(nameof(IJournalFormat), exception.Message);
    }

    [Fact]
    public void DurableService_ResolvesCommandCodecFromJournalFormatKey()
    {
        var storage = new VolatileJournalStorage();
        var wasUsed = false;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IGrainContext>(_ => new JournalBatchTests.TestGrainContext(GrainId.Create("test-grain", "keyed")));
        services.AddScoped<IJournalStorageProvider>(_ => new TestJournalStorageProvider(storage));
        services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = CustomFormatKey);
        services.AddScoped<JournaledStateManagerShared>();
        services.AddScoped<IJournaledStateManager, JournaledStateManager>();
        services.AddKeyedScoped(typeof(IDurableValue<>), KeyedService.AnyKey, typeof(DurableValue<>));
        services.AddKeyedSingleton<IJournalFormat>(CustomFormatKey, new TestJournalFormat());
        services.AddKeyedSingleton<IDurableDictionaryCommandCodec<string, uint>>(CustomFormatKey, new TestDictionaryCodec<string, uint>());
        services.AddKeyedSingleton<IDurableDictionaryCommandCodec<string, DateTime>>(CustomFormatKey, new TestDictionaryCodec<string, DateTime>());
        services.AddKeyedSingleton<IDurableValueCommandCodec<int>>(CustomFormatKey, (_, _) =>
        {
            wasUsed = true;
            return new TestValueCodec<int>();
        });

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        _ = scope.ServiceProvider.GetRequiredKeyedService<IDurableValue<int>>("value");

        Assert.True(wasUsed);
    }

    [Fact]
    public async Task StateManagerFactory_CreatesManagerForJournalId()
    {
        var storage = new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey);
        var builder = new TestSiloBuilder();
        builder.Services.AddSerializer();
        builder.Services.AddLogging();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.AddJournalStorage();
        builder.Services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey);
        builder.Services.AddScoped<IJournalStorageProvider>(_ => new TestJournalStorageProvider(storage));

        using var serviceProvider = builder.Services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IJournaledStateManagerFactory>()
            .Create(new JournalId("on-demand-journal"));
        await using (manager.ConfigureAwait(false))
        {
            var codecProvider = scope.ServiceProvider.GetRequiredService<ICodecProvider>();
            var sessionPool = scope.ServiceProvider.GetRequiredService<SerializerSessionPool>();
            var value = new DurableValue<int>(
                "value",
                manager,
                new OrleansBinaryDurableValueCommandCodec<int>(codecProvider.GetCodec<int>(), sessionPool));

            await manager.InitializeAsync(CancellationToken.None);
            value.Value = 42;
            await manager.WriteStateAsync(CancellationToken.None);
        }

        Assert.NotEmpty(storage.Segments);
    }

    private sealed class TestJournalStorageProvider(IJournalStorage storage) : IJournalStorageProvider
    {
        public IJournalStorage CreateStorage(JournalId journalId) => storage;
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

        public JournalBufferWriter CreateWriter() => new OrleansBinaryJournalBufferWriter();

        public void Replay(JournalBufferReader input, JournalReplayContext context) => throw new NotSupportedException();
    }

    private sealed class TestDictionaryCodec<TKey, TValue> : IDurableDictionaryCommandCodec<TKey, TValue>
        where TKey : notnull
    {
        public void WriteSet(TKey key, TValue value, JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteRemove(TKey key, JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteClear(JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, JournalStreamWriter writer) => throw new NotSupportedException();

        public void Apply(JournalBufferReader input, IDurableDictionaryCommandHandler<TKey, TValue> consumer) => throw new NotSupportedException();
    }

    private sealed class TestValueCodec<T> : IDurableValueCommandCodec<T>
    {
        public void WriteSet(T value, JournalStreamWriter writer) => throw new NotSupportedException();

        public void Apply(JournalBufferReader input, IDurableValueCommandHandler<T> consumer) => throw new NotSupportedException();
    }
}
