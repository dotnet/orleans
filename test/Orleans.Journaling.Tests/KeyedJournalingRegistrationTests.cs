using System.Buffers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Journaling.Json;
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

        builder.AddJournalStorage();

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var jsonFormat = Assert.IsType<JsonLinesJournalFormat>(serviceProvider.GetRequiredKeyedService<IJournalFormat>(JsonJournalExtensions.JournalFormatKey));
        Assert.Same(jsonFormat, serviceProvider.GetRequiredService<IJournalFormat>());
        Assert.Equal(".jsonl", jsonFormat.FileExtension);
        Assert.Equal("application/jsonl", jsonFormat.MimeType);
        CodecTestHelpers.AssertCodecProviderRegistrations(
            serviceProvider,
            JsonJournalExtensions.JournalFormatKey,
            serviceProvider.GetRequiredService<JsonOperationCodecProvider>(),
            expectDefaultProvider: true);

        Assert.Same(OrleansBinaryJournalFormat.Instance, serviceProvider.GetRequiredKeyedService<IJournalFormat>(OrleansBinaryJournalFormat.JournalFormatKey));
        Assert.Equal(".bin", OrleansBinaryJournalFormat.Instance.FileExtension);
        Assert.Equal("application/vnd.microsoft.orleans.journal+binary", OrleansBinaryJournalFormat.Instance.MimeType);
        CodecTestHelpers.AssertCodecProviderRegistrations(
            serviceProvider,
            OrleansBinaryJournalFormat.JournalFormatKey,
            serviceProvider.GetRequiredService<OrleansBinaryOperationCodecProvider>(),
            expectDefaultProvider: false);
    }

    [Fact]
    public void StateMachineManager_MissingKeyedFormat_ThrowsClearConfigurationError()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var storage = new VolatileJournalStorage();

        var exception = Assert.Throws<InvalidOperationException>(() => new JournalStateMachineManager(
            storage,
            LoggerFactory.CreateLogger<JournalStateMachineManager>(),
            Options.Create(ManagerOptions),
            TimeProvider.System,
            serviceProvider,
            CustomFormatKey));

        Assert.Contains(CustomFormatKey, exception.Message);
        Assert.Contains(nameof(IJournalFormat), exception.Message);
    }

    [Fact]
    public void DurableService_ResolvesCodecProviderFromJournalFormatKey()
    {
        var storage = new VolatileJournalStorage();
        var valueProvider = new TestValueCodecProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IJournalStorage>(_ => storage);
        services.AddKeyedScoped<string>(JournalFormatServices.JournalFormatKeyServiceKey, (_, _) => CustomFormatKey);
        services.AddScoped<IStateMachineManager, JournalStateMachineManager>();
        services.AddKeyedScoped(typeof(IDurableValue<>), KeyedService.AnyKey, typeof(DurableValue<>));
        services.AddKeyedSingleton<IJournalFormat>(CustomFormatKey, new TestJournalFormat());
        services.AddKeyedSingleton<IDurableDictionaryOperationCodecProvider>(CustomFormatKey, new TestDictionaryCodecProvider());
        services.AddKeyedSingleton<IDurableValueOperationCodecProvider>(CustomFormatKey, valueProvider);

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        _ = scope.ServiceProvider.GetRequiredKeyedService<IDurableValue<int>>("value");

        Assert.True(valueProvider.WasUsed);
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class TestJournalFormat : IJournalFormat
    {
        public string FileExtension => ".test";

        public string? MimeType => null;

        public IJournalBatchWriter CreateWriter() => throw new NotSupportedException();

        public void Read(JournalReadBuffer input, IStateMachineResolver resolver) => throw new NotSupportedException();
    }

    private sealed class TestDictionaryCodecProvider : IDurableDictionaryOperationCodecProvider
    {
        public IDurableDictionaryOperationCodec<TKey, TValue> GetCodec<TKey, TValue>()
            where TKey : notnull
            => new TestDictionaryCodec<TKey, TValue>();
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

    private sealed class TestValueCodecProvider : IDurableValueOperationCodecProvider
    {
        public bool WasUsed { get; private set; }

        public IDurableValueOperationCodec<T> GetCodec<T>()
        {
            WasUsed = true;
            return new TestValueCodec<T>();
        }
    }

    private sealed class TestValueCodec<T> : IDurableValueOperationCodec<T>
    {
        public void WriteSet(T value, JournalStreamWriter writer) => throw new NotSupportedException();

        public void Apply(ReadOnlySequence<byte> input, IDurableValueOperationHandler<T> consumer) => throw new NotSupportedException();
    }
}
