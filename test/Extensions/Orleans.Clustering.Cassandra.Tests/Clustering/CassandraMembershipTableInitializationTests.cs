using System.Reflection;
using Cassandra;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Clustering.Cassandra;
using Orleans.Clustering.Cassandra.Hosting;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace Tester.Cassandra.Clustering;

[TestSuite("BVT")]
[TestProvider("Cassandra")]
[TestArea("Membership")]
[TestCategory("BVT")]
public sealed class CassandraMembershipTableInitializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembershipWriteReceipts_PreCanceled_DoNotCreateSession(bool update)
    {
        var backend = new InitializationBackend();
        using var table = backend.CreateTable(false);
        backend.Session.ClearReceivedCalls();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var version = new TableVersion(1, "0");

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update
            ? table.UpdateRowWithResultAsync(null!, "0", version, cancellation.Token)
            : table.InsertRowWithResultAsync(null!, version, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, backend.FactoryCalls);
        Assert.Empty(backend.Session.ReceivedCalls());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialize_ConfiguredTtl_CreatesPersistentTable(bool gateway)
    {
        var backend = new InitializationBackend { SchemaRead = () => Task.FromResult<RowSet>(new BufferedRowSet()) };
        var options = new CassandraClusteringOptions { UseCassandraTtl = true };
        options.ConfigureClient(_ => Task.FromResult(backend.Session));
        var clusterOptions = Options.Create(new ClusterOptions { ServiceId = "service", ClusterId = "cluster" });
        var membershipOptions = Options.Create(new ClusterMembershipOptions());
        var providerOptions = Options.Create(options);
        var services = Substitute.For<IServiceProvider>();
        using var table = new CassandraClusteringTable(clusterOptions, providerOptions, membershipOptions, services);
        IGatewayListProvider gatewayProvider = new CassandraGatewayListProvider(
            clusterOptions, Options.Create(new GatewayOptions()), providerOptions, membershipOptions, services);

        if (gateway)
        {
            await gatewayProvider.InitializeGatewayListProvider();
        }
        else
        {
            await table.InitializeMembershipTableAsync(true, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, backend.SchemaChecks);
        Assert.Contains("default_time_to_live = 0", backend.CreateTableQuery);
        Assert.Equal(gateway ? null : 0, backend.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialize_Repeated_ReusesSessionAndBootstrapsAfterDelete(bool ownsSession)
    {
        var backend = new InitializationBackend();
        using var table = backend.CreateTable(ownsSession);
        var token = TestContext.Current.CancellationToken;

        await table.InitializeMembershipTableAsync(false, token);
        Assert.Null(backend.Version);
        await table.InitializeMembershipTableAsync(true, token);
        Assert.Equal(0, backend.Version);
        backend.Version = 17;
        await table.InitializeMembershipTableAsync(true, token);
        Assert.Equal(17, backend.Version);

        await table.DeleteMembershipTableEntriesAsync("cluster", token);
        Assert.Null(backend.Version);
        await table.InitializeMembershipTableAsync(true, token);

        Assert.Equal(0, backend.Version);
        Assert.Equal(1, backend.FactoryCalls);
        Assert.Equal(4, backend.SchemaChecks);
        Assert.Equal(3, backend.VersionChecks);
        Assert.Equal(2, backend.VersionInserts);
        Assert.Equal(1, backend.Deletes);
        backend.Cluster.DidNotReceive().Dispose();
        table.Dispose();
        table.Dispose();
        backend.Cluster.Received(ownsSession ? 1 : 0).Dispose();
        backend.Session.DidNotReceive().Dispose();
    }

    [Fact]
    public async Task Initialize_OverlappingCalls_CreateOneSessionAndBootstrapEachCall()
    {
        var backend = new InitializationBackend();
        var creation = new TaskCompletionSource<ISession>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var table = backend.CreateTable(true, () => creation.Task);
        var token = TestContext.Current.CancellationToken;
        var first = table.InitializeMembershipTableAsync(true, token);
        var second = table.InitializeMembershipTableAsync(true, token);

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, backend.FactoryCalls);
        creation.SetResult(backend.Session);
        await Task.WhenAll(first, second).WaitAsync(token);

        Assert.Equal(1, backend.FactoryCalls);
        Assert.Equal(2, backend.SchemaChecks);
        Assert.Equal(2, backend.VersionChecks);
        Assert.Equal(1, backend.VersionInserts);
        Assert.Equal(0, backend.Version);
    }

    [Fact]
    public async Task Initialize_CanceledWhileWaiting_DoesNotCreateSessionOrBootstrap()
    {
        var backend = new InitializationBackend();
        var creation = new TaskCompletionSource<ISession>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var table = backend.CreateTable(true, () => creation.Task);
        var token = TestContext.Current.CancellationToken;
        var first = table.InitializeMembershipTableAsync(true, token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var waiting = table.InitializeMembershipTableAsync(true, cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(first.IsCompleted);
        Assert.Equal(1, backend.FactoryCalls);
        creation.SetResult(backend.Session);
        await first.WaitAsync(token);

        Assert.Equal(1, backend.FactoryCalls);
        Assert.Equal(1, backend.SchemaChecks);
        Assert.Equal(1, backend.VersionChecks);
        Assert.Equal(1, backend.VersionInserts);
    }

    [Fact]
    public async Task Initialize_RepeatedCancellation_RetainsOwnedSessionForRetry()
    {
        var backend = new InitializationBackend();
        using var table = backend.CreateTable(true);
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var schemaRead = new TaskCompletionSource<RowSet>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.SchemaRead = () => schemaRead.Task;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var repeated = table.InitializeMembershipTableAsync(true, cancellation.Token);
        Assert.False(repeated.IsCompleted);

        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repeated.WaitAsync(token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(schemaRead.Task.IsCompleted);
        Assert.Equal(1, backend.FactoryCalls);
        Assert.Equal(1, backend.VersionChecks);
        backend.Cluster.DidNotReceive().Dispose();

        schemaRead.SetResult(new BufferedRowSet(new Row()));
        backend.SchemaRead = null;
        await table.InitializeMembershipTableAsync(true, token);
        Assert.Equal(1, backend.FactoryCalls);
        Assert.Equal(3, backend.SchemaChecks);
        Assert.Equal(2, backend.VersionChecks);
        Assert.Equal(0, backend.Version);
    }

    [Fact]
    public async Task Initialize_RepeatedFailure_RetainsOwnedSessionForRetry()
    {
        var backend = new InitializationBackend();
        using var table = backend.CreateTable(true);
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var failure = new InvalidOperationException("Bootstrap failed.");
        backend.SchemaRead = () => Task.FromException<RowSet>(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => table.InitializeMembershipTableAsync(true, token)));
        backend.Cluster.DidNotReceive().Dispose();
        Assert.Equal(1, backend.VersionChecks);

        backend.SchemaRead = null;
        await table.InitializeMembershipTableAsync(true, token);
        Assert.Equal(1, backend.FactoryCalls);
        Assert.Equal(3, backend.SchemaChecks);
        Assert.Equal(2, backend.VersionChecks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialize_FirstBootstrapFailure_DisposesOnlyOwnedSession(bool ownsSession)
    {
        var backend = new InitializationBackend();
        var failure = new InvalidOperationException("Bootstrap failed.");
        backend.SchemaRead = () => Task.FromException<RowSet>(failure);
        using var table = backend.CreateTable(ownsSession);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => table.InitializeMembershipTableAsync(true, TestContext.Current.CancellationToken)));

        Assert.Equal(1, backend.FactoryCalls);
        Assert.Equal(0, backend.VersionChecks);
        backend.Cluster.Received(ownsSession ? 1 : 0).Dispose();
        table.Dispose();
        backend.Cluster.Received(ownsSession ? 1 : 0).Dispose();
    }

    [Fact]
    public async Task Initialize_CanceledDuringFactory_DisposesLateOwnedSession()
    {
        var backend = new InitializationBackend();
        var creation = new TaskCompletionSource<ISession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Cluster.When(cluster => cluster.Dispose()).Do(_ => disposed.TrySetResult());
        using var table = backend.CreateTable(true, () => creation.Task);
        var token = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var initialization = table.InitializeMembershipTableAsync(true, cancellation.Token);
        Assert.False(initialization.IsCompleted);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization.WaitAsync(token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(creation.Task.IsCompleted);
        table.Dispose();
        creation.SetResult(backend.Session);
        await disposed.Task.WaitAsync(token);

        Assert.Equal(1, backend.FactoryCalls);
        Assert.Equal(0, backend.SchemaChecks);
        backend.Cluster.Received(1).Dispose();
    }

    [Fact]
    public async Task Initialize_DisposedDuringFactory_DisposesOwnedSessionAndRejectsPublication()
    {
        var backend = new InitializationBackend();
        var creation = new TaskCompletionSource<ISession>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var table = backend.CreateTable(true, () => creation.Task);
        var initialization = table.InitializeMembershipTableAsync(true, TestContext.Current.CancellationToken);
        table.Dispose();
        creation.SetResult(backend.Session);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => initialization);
        backend.Cluster.Received(1).Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => table.InitializeMembershipTableAsync(true, TestContext.Current.CancellationToken));
        Assert.Equal(1, backend.FactoryCalls);
    }

    private sealed class InitializationBackend
    {
        private readonly Dictionary<IStatement, string> _statements = [];
        public ISession Session { get; } = Substitute.For<ISession>();
        public ICluster Cluster { get; } = Substitute.For<ICluster>();
        public int FactoryCalls { get; private set; }
        public int SchemaChecks { get; private set; }
        public int VersionChecks { get; private set; }
        public int VersionInserts { get; private set; }
        public int Deletes { get; private set; }
        public int? Version { get; set; }
        public Func<Task<RowSet>>? SchemaRead { get; set; }
        public string? CreateTableQuery { get; private set; }

        public InitializationBackend()
        {
            Session.Cluster.Returns(Cluster);
            Session.Keyspace.Returns("orleans");
            Session.PrepareAsync(Arg.Any<string>()).Returns(call =>
            {
                var query = call.Arg<string>();
                var prepared = Substitute.For<PreparedStatement>();
                prepared.Bind(Arg.Any<object[]>()).Returns(_ =>
                {
                    var statement = new BoundStatement();
                    _statements.Add(statement, query);
                    return statement;
                });
                return Task.FromResult(prepared);
            });
            Session.ExecuteAsync(Arg.Any<IStatement>()).Returns(call =>
            {
                var statement = call.Arg<IStatement>();
                var query = statement is SimpleStatement simple ? simple.QueryString : _statements[statement];
                if (query.StartsWith("SELECT * FROM system_schema.tables", StringComparison.Ordinal))
                {
                    SchemaChecks++;
                    return SchemaRead?.Invoke() ?? Task.FromResult<RowSet>(new BufferedRowSet(new Row()));
                }

                if (query.StartsWith("CREATE TABLE", StringComparison.Ordinal))
                {
                    CreateTableQuery = query;
                    return Task.FromResult<RowSet>(new BufferedRowSet());
                }

                if (query.StartsWith("CREATE INDEX", StringComparison.Ordinal))
                {
                    return Task.FromResult<RowSet>(new BufferedRowSet());
                }

                if (query.StartsWith("SELECT version FROM membership", StringComparison.Ordinal))
                {
                    VersionChecks++;
                    return Task.FromResult<RowSet>(Version.HasValue ? new BufferedRowSet(new Row()) : new BufferedRowSet());
                }

                if (query.StartsWith("INSERT INTO membership(", StringComparison.Ordinal))
                {
                    VersionInserts++;
                    Version ??= 0;
                    return Task.FromResult<RowSet>(new BufferedRowSet());
                }

                if (query.StartsWith("DELETE FROM membership", StringComparison.Ordinal))
                {
                    Deletes++;
                    Version = null;
                    return Task.FromResult<RowSet>(new BufferedRowSet());
                }

                throw new InvalidOperationException($"Unexpected initialization query: {query}");
            });
        }

        public CassandraClusteringTable CreateTable(bool ownsSession, Func<Task<ISession>>? factory = null)
        {
            var options = new CassandraClusteringOptions();
            Task<ISession> CreateSession(IServiceProvider _)
            {
                FactoryCalls++;
                return factory?.Invoke() ?? Task.FromResult(Session);
            }

            if (ownsSession)
            {
                options.ConfigureClient("127.0.0.1");
                // Keep the built-in ownership policy while replacing network creation with a controlled session.
                typeof(CassandraClusteringOptions)
                    .GetProperty("CreateSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(options, (Func<IServiceProvider, Task<ISession>>)CreateSession);
            }
            else
            {
                options.ConfigureClient(CreateSession);
            }

            return new CassandraClusteringTable(
                Options.Create(new ClusterOptions { ServiceId = "service", ClusterId = "cluster" }),
                Options.Create(options),
                Options.Create(new ClusterMembershipOptions()),
                Substitute.For<IServiceProvider>());
        }
    }

    private sealed class BufferedRowSet : RowSet
    {
        public BufferedRowSet(params Row[] rows)
        {
            foreach (var row in rows)
            {
                RowQueue.Enqueue(row);
            }
        }
    }
}
