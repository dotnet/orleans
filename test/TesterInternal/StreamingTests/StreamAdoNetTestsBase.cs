using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Streams;
using Orleans.TestingHost.Utils;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests;

/// <summary>
/// Holds test code common across all ADONET streaming implementations.
/// </summary>
[Collection(TestEnvironmentFixture.DefaultCollection)]
public abstract class StreamAdoNetTestsBase : IAsyncLifetime, IClassFixture<ConnectionStringFixture>
{
    protected StreamAdoNetTestsBase(ConnectionStringFixture connectionStringFixture, TestEnvironmentFixture testEnvironmentFixture, LoggerFilterOptions loggerFilterOptions)
    {
        ConnectionStringFixture = connectionStringFixture;
        ConnectionStringFixture.InitializeConnectionStringAccessor(GetConnectionString);

        TestEnvironmentFixture = testEnvironmentFixture;
        LoggerFilterOptions = loggerFilterOptions;

        LoggerFactory = TestingUtils.CreateDefaultLoggerFactory($"{GetType()}.log", loggerFilterOptions);
        _logger = LoggerFactory.CreateLogger<StreamAdoNetTestsBase>();

        ClusterOptions = Options.Create(new ClusterOptions
        {
            ServiceId = Guid.NewGuid().ToString(),
            ClusterId = Guid.NewGuid().ToString(),
        });

        QueueAdapter = CreateQueueAdapter();
        QueueAdapterReceiver = CreateQueueAdapterReceiver();
    }

    private readonly ILogger _logger;

    protected ConnectionStringFixture ConnectionStringFixture { get; }
    protected TestEnvironmentFixture TestEnvironmentFixture { get; }
    protected LoggerFilterOptions LoggerFilterOptions { get; }
    protected ILoggerFactory LoggerFactory { get; }
    protected IOptions<ClusterOptions> ClusterOptions { get; }
    protected IQueueAdapter QueueAdapter { get; }
    protected IQueueAdapterReceiver QueueAdapterReceiver { get; }

    protected string TestDatabaseName => "OrleansStreamingTest";

    #region Lifetime

    public virtual Task InitializeAsync() => Task.CompletedTask;

    public virtual Task DisposeAsync() => Task.CompletedTask;

    #endregion Lifetime

    #region Derived Artefacts

    protected abstract Task<string> GetConnectionString();

    protected abstract IQueueAdapter CreateQueueAdapter();

    protected abstract IQueueAdapterReceiver CreateQueueAdapterReceiver();

    protected abstract string GetAdoInvariant();

    #endregion Derived Artefacts
}