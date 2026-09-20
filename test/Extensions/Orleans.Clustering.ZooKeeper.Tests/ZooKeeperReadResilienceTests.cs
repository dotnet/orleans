using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Clustering.TestKit;
using Orleans.Configuration;
using Orleans.Runtime.Membership;
using Orleans.TestingHost.Utils;
using org.apache.zookeeper;
using TestExtensions;
using Xunit;

namespace UnitTests.MembershipTests;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("Membership"), TestCategory("ZooKeeper")]
[TestSuite("Functional"), TestProvider("ZooKeeper"), TestArea("Membership")]
public sealed class ZooKeeperReadResilienceTests : IAsyncLifetime
{
    private readonly ZooKeeperNativeDiagnostics _diagnostics = new();
    private readonly ConcurrentBag<ZooKeeperSession> _sessions = [];
    private readonly List<MembershipTableTestFixture> _fixtures = [];
    private readonly ConcurrentBag<Task> _legacyOperations = [];
    private readonly ConcurrentBag<Task> _probes = [];
    private readonly string _socketLog = Path.Combine(AppContext.BaseDirectory, "TestResults", $"zookeeper-sockets-{Guid.NewGuid():N}.log");
    private readonly ILoggerFactory _loggerFactory = TestingUtils.CreateDefaultLoggerFactory(
        $"zookeeper-reads-{Guid.NewGuid():N}.log", new LoggerFilterOptions());
    private string _connectionString = null!;

    public async ValueTask InitializeAsync()
    {
        Assert.False(string.IsNullOrWhiteSpace(TestDefaultConfiguration.ZooKeeperConnectionString),
            "ZooKeeper resilience tests require a configured connection string.");
        _connectionString = TestDefaultConfiguration.ZooKeeperConnectionString!;
        var probe = ZooKeeper.Using(_connectionString, 2000, new ConformanceWatcher(),
            async client => await client.existsAsync("/", false) is not null);
        _probes.Add(probe);
        Assert.True(await probe.WaitAsync(TestContext.Current.CancellationToken),
            "ZooKeeper resilience tests require the configured ZooKeeper service.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // Fixture teardown and canceled caller waits can return before native close.
            await Task.WhenAll(_fixtures.Select(fixture => DrainFixtureAsync(fixture.DisposeAsync))
                .Concat(_sessions.Select(session => session.Completion))
                .Concat(_legacyOperations)
                .Concat(_probes));
        }
        finally
        {
            _diagnostics.Dispose();
            await _diagnostics.WriteAsync(_socketLog);
            _loggerFactory.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembershipTable_ZooKeeper_RepeatedSnapshotReadCompatibility(bool pointRead)
    {
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var started = Stopwatch.GetTimestamp();
            await CreateFixture().RunAsync(async (fixture, cancellationToken) =>
            {
                var runner = new MembershipTableTestRunner(fixture, seed: 17, concurrencyRowCount: 128);
                if (pointRead)
                {
                    await runner.ConcurrentReadRow_ReturnsOnlyAtomicCommittedViews(cancellationToken);
                }
                else
                {
                    await runner.ConcurrentReadAll_ReturnsOnlyAtomicCommittedViews(cancellationToken);
                }

                var readStarted = Stopwatch.GetTimestamp();
                var snapshot = await fixture.First.ReadAllAsync(cancellationToken);
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"Stable snapshot rows={snapshot.Members.Count}; elapsed={Stopwatch.GetElapsedTime(readStarted)}");
            }, TestContext.Current.CancellationToken);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Snapshot compatibility pass {iteration + 1}/3; pointRead={pointRead}; elapsed={Stopwatch.GetElapsedTime(started)}");
        }
    }

    [Fact]
    public Task MembershipTable_ZooKeeper_DeletionProbe_PreservesOriginalScope() =>
        CreateFixture().RunAsync((fixture, cancellationToken) =>
            new MembershipTableTestRunner(fixture, seed: 17)
                .DeleteMembershipTableEntries_DeletesOwnClusterAndPreservesOtherCluster(cancellationToken),
            TestContext.Current.CancellationToken);

    internal static async Task DrainFixtureAsync(Func<ValueTask> dispose)
    {
        try
        {
            await dispose();
        }
        catch (TimeoutException exception) when (exception.Data["ClusteringTestKit.CleanupCompletion"] is Task completion)
        {
            await completion;
            throw;
        }
    }

    private MembershipTableTestFixture CreateFixture()
    {
        var fixture = new MembershipTableTestFixture(nameof(ZooKeeperReadResilienceTests), (serviceId, clusterId, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logger = _loggerFactory.CreateLogger<ZooKeeperBasedMembershipTable>();
            var sessions = new ConcurrentBag<ZooKeeperSession>();
            var legacyOperations = new ConcurrentBag<Task>();
            var table = new ZooKeeperBasedMembershipTable(
                logger,
                Options.Create(new ZooKeeperClusteringSiloOptions { ConnectionString = _connectionString }),
                Options.Create(new ClusterOptions { ServiceId = serviceId, ClusterId = clusterId }),
                readOnly =>
                {
                    var session = ZooKeeperBasedMembershipTable.CreateSession(
                        _connectionString + "/" + clusterId, new ZooKeeperWatcher(logger), readOnly);
                    sessions.Add(session);
                    _sessions.Add(session);
                    return session;
                },
                null,
                operation =>
                {
                    legacyOperations.Add(operation);
                    _legacyOperations.Add(operation);
                });
            return ValueTask.FromResult(new MembershipTableTestHandle(table,
                () => new ValueTask(Task.WhenAll(sessions.Select(session => session.Completion).Concat(legacyOperations)))));
        }, IsConformanceClusterDeletedAsync);
        _fixtures.Add(fixture);
        return fixture;
    }

    private async ValueTask<bool> IsConformanceClusterDeletedAsync(string clusterId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var probe = ZooKeeper.Using(_connectionString, 10_000, new ConformanceWatcher(), async client =>
        {
            await client.sync("/");
            cancellationToken.ThrowIfCancellationRequested();
            return await client.existsAsync("/" + clusterId, false) is null;
        });
        _probes.Add(probe);
        return await probe;
    }

    private sealed class ConformanceWatcher : Watcher
    {
        public override Task process(WatchedEvent @event) => Task.CompletedTask;
    }
}
