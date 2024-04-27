using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Streaming.AdoNet;
using Orleans.Streams;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using UnitTests.General;
using static System.String;
using RelationalOrleansQueries = Orleans.Streaming.AdoNet.Storage.RelationalOrleansQueries;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests for <see cref="AdoNetQueueAdapterFactory"/>.
/// </summary>
[Collection(TestEnvironmentFixture.DefaultCollection)]
public class AdoNetQueueAdapterFactoryTests(TestEnvironmentFixture fixture) : IAsyncLifetime
{
    private readonly TestEnvironmentFixture _fixture = fixture;
    private RelationalStorageForTesting _testing;
    private IRelationalStorage _storage;
    private RelationalOrleansQueries _queries;

    private const string TestDatabaseName = "OrleansStreamTest";
    private const string AdoNetInvariantName = AdoNetInvariants.InvariantNameSqlServer;

    public async Task InitializeAsync()
    {
        _testing = await RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName);
        Skip.If(IsNullOrEmpty(_testing.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

        _storage = _testing.Storage;
        _queries = await RelationalOrleansQueries.CreateInstance(AdoNetInvariantName, _testing.CurrentConnectionString);
    }

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

    public Task DisposeAsync() => Task.CompletedTask;
}