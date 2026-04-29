using System.Buffers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class KeyedJournalingRegistrationTests : StateMachineTestBase
{
    private const string CustomFormatKey = "custom-test-format";

    [Fact]
    public void AddStateMachineStorage_RegistersBinaryFamilyByFormatKey()
    {
        var builder = new TestSiloBuilder();

        builder.AddStateMachineStorage();

        using var serviceProvider = builder.Services.BuildServiceProvider();
        Assert.Same(BinaryLogExtentCodec.Instance, serviceProvider.GetRequiredKeyedService<IStateMachineLogFormat>(StateMachineLogFormatKeys.OrleansBinary));
        Assert.Same(serviceProvider.GetRequiredService<OrleansBinaryLogEntryCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableDictionaryCodecProvider>(StateMachineLogFormatKeys.OrleansBinary));
        Assert.Same(serviceProvider.GetRequiredService<OrleansBinaryLogEntryCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableListCodecProvider>(StateMachineLogFormatKeys.OrleansBinary));
        Assert.Same(serviceProvider.GetRequiredService<OrleansBinaryLogEntryCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableQueueCodecProvider>(StateMachineLogFormatKeys.OrleansBinary));
        Assert.Same(serviceProvider.GetRequiredService<OrleansBinaryLogEntryCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableSetCodecProvider>(StateMachineLogFormatKeys.OrleansBinary));
        Assert.Same(serviceProvider.GetRequiredService<OrleansBinaryLogEntryCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableValueCodecProvider>(StateMachineLogFormatKeys.OrleansBinary));
        Assert.Same(serviceProvider.GetRequiredService<OrleansBinaryLogEntryCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableStateCodecProvider>(StateMachineLogFormatKeys.OrleansBinary));
        Assert.Same(serviceProvider.GetRequiredService<OrleansBinaryLogEntryCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableTaskCompletionSourceCodecProvider>(StateMachineLogFormatKeys.OrleansBinary));
    }

    [Fact]
    public void StateMachineManager_MissingKeyedFormat_ThrowsClearConfigurationError()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var storage = new VolatileStateMachineStorage(CustomFormatKey);

        var exception = Assert.Throws<InvalidOperationException>(() => new StateMachineManager(
            storage,
            LoggerFactory.CreateLogger<StateMachineManager>(),
            Options.Create(ManagerOptions),
            TimeProvider.System,
            serviceProvider));

        Assert.Contains(CustomFormatKey, exception.Message);
        Assert.Contains(nameof(IStateMachineLogFormat), exception.Message);
    }

    [Fact]
    public void DurableService_ResolvesCodecProviderFromStorageFormatKey()
    {
        var storage = new VolatileStateMachineStorage(CustomFormatKey);
        var valueProvider = new TestValueCodecProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IStateMachineStorage>(_ => storage);
        services.AddScoped<IStateMachineManager, StateMachineManager>();
        services.AddKeyedScoped(typeof(IDurableValue<>), KeyedService.AnyKey, typeof(DurableValue<>));
        services.AddKeyedSingleton<IStateMachineLogFormat>(CustomFormatKey, new TestLogFormat());
        services.AddKeyedSingleton<IDurableDictionaryCodecProvider>(CustomFormatKey, new TestDictionaryCodecProvider());
        services.AddKeyedSingleton<IDurableValueCodecProvider>(CustomFormatKey, valueProvider);

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

    private sealed class TestLogFormat : IStateMachineLogFormat
    {
        public IStateMachineLogExtentWriter CreateWriter() => throw new NotSupportedException();

        public void Read(ArcBuffer input, IStateMachineLogEntryConsumer consumer) => throw new NotSupportedException();
    }

    private sealed class TestDictionaryCodecProvider : IDurableDictionaryCodecProvider
    {
        public IDurableDictionaryCodec<TKey, TValue> GetCodec<TKey, TValue>()
            where TKey : notnull
            => new TestDictionaryCodec<TKey, TValue>();
    }

    private sealed class TestDictionaryCodec<TKey, TValue> : IDurableDictionaryCodec<TKey, TValue>
        where TKey : notnull
    {
        public void WriteSet(TKey key, TValue value, IBufferWriter<byte> output) => throw new NotSupportedException();

        public void WriteRemove(TKey key, IBufferWriter<byte> output) => throw new NotSupportedException();

        public void WriteClear(IBufferWriter<byte> output) => throw new NotSupportedException();

        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, IBufferWriter<byte> output) => throw new NotSupportedException();

        public void Apply(ReadOnlySequence<byte> input, IDurableDictionaryLogEntryConsumer<TKey, TValue> consumer) => throw new NotSupportedException();
    }

    private sealed class TestValueCodecProvider : IDurableValueCodecProvider
    {
        public bool WasUsed { get; private set; }

        public IDurableValueCodec<T> GetCodec<T>()
        {
            WasUsed = true;
            return new TestValueCodec<T>();
        }
    }

    private sealed class TestValueCodec<T> : IDurableValueCodec<T>
    {
        public void WriteSet(T value, IBufferWriter<byte> output) => throw new NotSupportedException();

        public void Apply(ReadOnlySequence<byte> input, IDurableValueLogEntryConsumer<T> consumer) => throw new NotSupportedException();
    }
}
