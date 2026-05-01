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
public sealed class KeyedJournalingRegistrationTests : JournalingTestBase
{
    private const string CustomFormatKey = "custom-test-format";

    [Fact]
    public void AddLogStorage_RegistersBinaryFamilyByFormatKey()
    {
        var builder = new TestSiloBuilder();

        builder.AddLogStorage();

        using var serviceProvider = builder.Services.BuildServiceProvider();
        Assert.Same(OrleansBinaryLogFormat.Instance, serviceProvider.GetRequiredKeyedService<ILogFormat>(OrleansBinaryLogFormat.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<OrleansBinaryOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableDictionaryOperationCodecProvider>(OrleansBinaryLogFormat.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<OrleansBinaryOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableListOperationCodecProvider>(OrleansBinaryLogFormat.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<OrleansBinaryOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableQueueOperationCodecProvider>(OrleansBinaryLogFormat.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<OrleansBinaryOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableSetOperationCodecProvider>(OrleansBinaryLogFormat.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<OrleansBinaryOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableValueOperationCodecProvider>(OrleansBinaryLogFormat.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<OrleansBinaryOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableStateOperationCodecProvider>(OrleansBinaryLogFormat.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<OrleansBinaryOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableTaskCompletionSourceOperationCodecProvider>(OrleansBinaryLogFormat.LogFormatKey));
    }

    [Fact]
    public void LogManager_MissingKeyedFormat_ThrowsClearConfigurationError()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var storage = new VolatileLogStorage();

        var exception = Assert.Throws<InvalidOperationException>(() => new LogManager(
            storage,
            LoggerFactory.CreateLogger<LogManager>(),
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
        services.AddScoped<ILogManager, LogManager>();
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
        public ILogSegmentWriter CreateWriter() => throw new NotSupportedException();

        public bool TryRead(ArcBufferReader input, ILogStreamStateMachineResolver resolver, bool isCompleted) => throw new NotSupportedException();
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
        public void WriteSet(TKey key, TValue value, IBufferWriter<byte> output) => throw new NotSupportedException();

        public void WriteRemove(TKey key, IBufferWriter<byte> output) => throw new NotSupportedException();

        public void WriteClear(IBufferWriter<byte> output) => throw new NotSupportedException();

        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, IBufferWriter<byte> output) => throw new NotSupportedException();

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
        public void WriteSet(T value, IBufferWriter<byte> output) => throw new NotSupportedException();

        public void Apply(ReadOnlySequence<byte> input, IDurableValueOperationHandler<T> consumer) => throw new NotSupportedException();
    }
}
