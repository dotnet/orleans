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
    public void AddLogStorage_RegistersJsonFamilyByDefaultAndBinaryFamilyByFormatKey()
    {
        var builder = new TestSiloBuilder();

        builder.AddLogStorage();

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var jsonFormat = Assert.IsType<JsonLinesLogFormat>(serviceProvider.GetRequiredKeyedService<ILogFormat>(JsonJournalingExtensions.LogFormatKey));
        Assert.Same(jsonFormat, serviceProvider.GetRequiredService<ILogFormat>());
        CodecTestHelpers.AssertCodecProviderRegistrations(
            serviceProvider,
            JsonJournalingExtensions.LogFormatKey,
            serviceProvider.GetRequiredService<JsonOperationCodecProvider>(),
            expectDefaultProvider: true);

        Assert.Same(OrleansBinaryLogFormat.Instance, serviceProvider.GetRequiredKeyedService<ILogFormat>(OrleansBinaryLogFormat.LogFormatKey));
        CodecTestHelpers.AssertCodecProviderRegistrations(
            serviceProvider,
            OrleansBinaryLogFormat.LogFormatKey,
            serviceProvider.GetRequiredService<OrleansBinaryOperationCodecProvider>(),
            expectDefaultProvider: false);
    }

    [Fact]
    public void StateMachineManager_MissingKeyedFormat_ThrowsClearConfigurationError()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var storage = new VolatileLogStorage();

        var exception = Assert.Throws<InvalidOperationException>(() => new LogStateMachineManager(
            storage,
            LoggerFactory.CreateLogger<LogStateMachineManager>(),
            Options.Create(ManagerOptions),
            TimeProvider.System,
            serviceProvider,
            CustomFormatKey));

        Assert.Contains(CustomFormatKey, exception.Message);
        Assert.Contains(nameof(ILogFormat), exception.Message);
    }

    [Fact]
    public void DurableService_ResolvesCodecProviderFromLogFormatKey()
    {
        var storage = new VolatileLogStorage();
        var valueProvider = new TestValueCodecProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ILogStorage>(_ => storage);
        services.AddKeyedScoped<string>(LogFormatServices.LogFormatKeyServiceKey, (_, _) => CustomFormatKey);
        services.AddScoped<IStateMachineManager, LogStateMachineManager>();
        services.AddKeyedScoped(typeof(IDurableValue<>), KeyedService.AnyKey, typeof(DurableValue<>));
        services.AddKeyedSingleton<ILogFormat>(CustomFormatKey, new TestLogFormat());
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

    private sealed class TestLogFormat : ILogFormat
    {
        public ILogBatchWriter CreateWriter() => throw new NotSupportedException();

        public void Read(LogReadBuffer input, IStateMachineResolver resolver) => throw new NotSupportedException();
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
        public void WriteSet(TKey key, TValue value, LogStreamWriter writer) => throw new NotSupportedException();

        public void WriteRemove(TKey key, LogStreamWriter writer) => throw new NotSupportedException();

        public void WriteClear(LogStreamWriter writer) => throw new NotSupportedException();

        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, LogStreamWriter writer) => throw new NotSupportedException();

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
        public void WriteSet(T value, LogStreamWriter writer) => throw new NotSupportedException();

        public void Apply(ReadOnlySequence<byte> input, IDurableValueOperationHandler<T> consumer) => throw new NotSupportedException();
    }
}
