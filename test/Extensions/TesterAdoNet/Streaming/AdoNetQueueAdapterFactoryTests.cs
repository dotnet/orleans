using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Streaming.AdoNet;
using Orleans.Streams;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using UnitTests.General;
using static System.String;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests for <see cref="AdoNetQueueAdapterFactory"/>.
/// </summary>
[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("AdoNet"), TestCategory("Streaming")]
public class AdoNetQueueAdapterFactoryTests(TestEnvironmentFixture fixture) : IAsyncLifetime
{
    private readonly TestEnvironmentFixture _fixture = fixture;
    private RelationalStorageForTesting _testing;
    private IRelationalStorage _storage;

    private const string TestDatabaseName = "OrleansStreamTest";
    private const string AdoNetInvariantName = AdoNetInvariants.InvariantNameSqlServer;

    public async Task InitializeAsync()
    {
        _testing = await RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName);
        Skip.If(IsNullOrEmpty(_testing.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

        _storage = _testing.Storage;
    }

    /// <summary>
    /// Tests that the <see cref="AdoNetQueueAdapterFactory"/> creates an <see cref="AdoNetQueueAdapter"/> instance.
    /// </summary>
    [SkippableFact]
    public async Task AdoNetQueueAdapterFactory_CreatesAdapter()
    {
        // arrange
        var name = "MyProviderName";
        var streamOptions = new AdoNetStreamOptions
        {
            Invariant = AdoNetInvariantName,
            ConnectionString = _storage.ConnectionString
        };
        var clusterOptions = new ClusterOptions
        {
            ServiceId = "MyServiceId"
        };
        var cacheOptions = new SimpleQueueCacheOptions();
        var hashOptions = new HashRingStreamQueueMapperOptions();
        var loggerFactory = NullLoggerFactory.Instance;
        var serviceProvider = _fixture.Services;
        var factory = new AdoNetQueueAdapterFactory(name, streamOptions, clusterOptions, cacheOptions, hashOptions, loggerFactory, serviceProvider);

        // act
        var adapter = await factory.CreateAdapter();

        // assert
        Assert.NotNull(adapter);
        Assert.IsType<AdoNetQueueAdapter>(adapter);
        Assert.Equal(name, adapter.Name);
        Assert.False(adapter.IsRewindable);
        Assert.Equal(StreamProviderDirection.ReadWrite, adapter.Direction);
    }

    /// <summary>
    /// Tests that the <see cref="AdoNetQueueAdapterFactory"/> gets a <see cref="AdoNetStreamFailureHandler"/> instance.
    /// </summary>
    [SkippableFact]
    public async Task AdoNetQueueAdapterFactory_GetsDeliveryFailureHandler()
    {
        // arrange
        var name = "MyProviderName";
        var streamOptions = new AdoNetStreamOptions
        {
            Invariant = AdoNetInvariantName,
            ConnectionString = _storage.ConnectionString
        };
        var clusterOptions = new ClusterOptions
        {
            ServiceId = "MyServiceId"
        };
        var cacheOptions = new SimpleQueueCacheOptions();
        var hashOptions = new HashRingStreamQueueMapperOptions();
        var loggerFactory = NullLoggerFactory.Instance;
        var serviceProvider = _fixture.Services;
        var factory = new AdoNetQueueAdapterFactory(name, streamOptions, clusterOptions, cacheOptions, hashOptions, loggerFactory, serviceProvider);
        var queueId = QueueId.GetQueueId("MyQueueName", 1, 2);

        // act
        var handler = await factory.GetDeliveryFailureHandler(queueId);

        // assert
        Assert.NotNull(handler);
        Assert.IsType<AdoNetStreamFailureHandler>(handler);
        Assert.False(handler.ShouldFaultSubsriptionOnError);
    }

    /// <summary>
    /// Tests that the <see cref="AdoNetQueueAdapterFactory"/> gets a <see cref="SimpleQueueCache"/> instance.
    /// </summary>
    [SkippableFact]
    public void AdoNetQueueAdapterFactory_GetsQueueAdapterCache()
    {
        // arrange
        var name = "MyProviderName";
        var streamOptions = new AdoNetStreamOptions
        {
            Invariant = AdoNetInvariantName,
            ConnectionString = _storage.ConnectionString
        };
        var clusterOptions = new ClusterOptions
        {
            ServiceId = "MyServiceId"
        };
        var cacheOptions = new SimpleQueueCacheOptions();
        var hashOptions = new HashRingStreamQueueMapperOptions();
        var loggerFactory = NullLoggerFactory.Instance;
        var serviceProvider = _fixture.Services;
        var factory = new AdoNetQueueAdapterFactory(name, streamOptions, clusterOptions, cacheOptions, hashOptions, loggerFactory, serviceProvider);

        // act
        var cache = factory.GetQueueAdapterCache();

        // assert
        Assert.NotNull(cache);
        Assert.IsType<SimpleQueueAdapterCache>(cache);
    }

    /// <summary>
    /// Tests that the <see cref="AdoNetQueueAdapterFactory"/> gets a <see cref="HashRingBasedStreamQueueMapper"/> instance.
    /// </summary>
    [SkippableFact]
    public void AdoNetQueueAdapterFactory_GetsStreamQueueMapper()
    {
        // arrange
        var name = "MyProviderName";
        var streamOptions = new AdoNetStreamOptions
        {
            Invariant = AdoNetInvariantName,
            ConnectionString = _storage.ConnectionString
        };
        var clusterOptions = new ClusterOptions
        {
            ServiceId = "MyServiceId"
        };
        var cacheOptions = new SimpleQueueCacheOptions();
        var hashOptions = new HashRingStreamQueueMapperOptions();
        var loggerFactory = NullLoggerFactory.Instance;
        var serviceProvider = _fixture.Services;
        var factory = new AdoNetQueueAdapterFactory(name, streamOptions, clusterOptions, cacheOptions, hashOptions, loggerFactory, serviceProvider);

        // act
        var mapper = factory.GetStreamQueueMapper();

        // assert
        Assert.NotNull(mapper);
        Assert.IsType<HashRingBasedStreamQueueMapper>(mapper);
    }

    /// <summary>
    /// Tests that the <see cref="AdoNetQueueAdapterFactory"/> constructs via its static factory method.
    /// </summary>
    [SkippableFact]
    public void AdoNetQueueAdapterFactory_ConstructsViaStaticFactory()
    {
        // arrange
        var name = "MyProviderName";

        // act
        var factory = AdoNetQueueAdapterFactory.Create(_fixture.Services, name);

        // assert
        Assert.NotNull(factory);
        Assert.IsType<AdoNetQueueAdapterFactory>(factory);
    }

    public Task DisposeAsync() => Task.CompletedTask;
}