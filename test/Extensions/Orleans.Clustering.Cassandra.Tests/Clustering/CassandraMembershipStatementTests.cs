using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Cassandra;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Clustering.Cassandra;
using Orleans.Clustering.Cassandra.Hosting;
using Orleans.Configuration;
using TestExtensions;
using Xunit;

namespace Tester.Cassandra.Clustering;

[TestSuite("BVT")]
[TestProvider("Cassandra")]
[TestArea("Membership")]
[TestCategory("BVT")]
public sealed class CassandraMembershipStatementTests
{
    private static readonly DateTime Timestamp = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerHeartbeat_UsesOneBlindColumnWrite(bool ttl)
    {
        var backend = new Backend { OnExecute = _ => Task.FromResult<RowSet>(new Rows()) };
        using var table = await backend.CreateTable(ttl);
        var entry = Entry(SiloStatus.Active);

        await table.UpdateIAmAliveAsync(entry, TestContext.Current.CancellationToken);

        var command = Assert.Single(backend.Executed);
        Assert.Equal(
            "UPDATE membership USING TTL 0 SET i_am_alive_time = :i_am_alive_time WHERE partition_key = :partition_key AND address = :address AND port = :port AND generation = :generation;",
            command.Cql);
        Assert.Equal(5, command.Values.Count);
        Assert.Equal("service-cluster", command.Values["partition_key"]);
        Assert.Equal(entry.IAmAliveTime, command.Values["i_am_alive_time"]);
        Assert.Equal(entry.SiloAddress.Endpoint.Address.ToString(), command.Values["address"]);
        Assert.Equal(entry.SiloAddress.Endpoint.Port, command.Values["port"]);
        Assert.Equal(entry.SiloAddress.Generation, command.Values["generation"]);
        Assert.Equal(ConsistencyLevel.Quorum, command.Statement.ConsistencyLevel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardedUpdate_UsesOneSerialRowReadAndOneConditionalWrite(bool ttl)
    {
        var backend = new Backend { Version = 5 };
        var existing = Entry(SiloStatus.Active);
        backend.Entries.Add(existing);
        backend.OnExecute = command => command.Cql.StartsWith("BEGIN BATCH", StringComparison.Ordinal)
            ? Task.FromResult<RowSet>(Rows.Applied(true))
            : null;
        using var table = await backend.CreateTable(ttl);
        var entry = Entry(SiloStatus.Active);
        entry.IAmAliveTime = Timestamp.AddSeconds(1);
        var token = TestContext.Current.CancellationToken;

        Assert.True(await table.UpdateRowAsync(entry, "5", new TableVersion(6, "5"), token));

        Assert.Collection(backend.Executed,
            AssertSingleRowRead,
            command =>
            {
                Assert.StartsWith("BEGIN BATCH", command.Cql);
                Assert.Equal(5, command.Values["expected_version"]);
                Assert.Equal(Timestamp, command.Values["previous_time"]);
                Assert.Equal(entry.IAmAliveTime, command.Values["i_am_alive_time"]);
                Assert.Equal(ConsistencyLevel.Serial, command.Statement.SerialConsistencyLevel);
                Assert.Equal(ConsistencyLevel.Quorum, command.Statement.ConsistencyLevel);
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardedUpdate_AbsentRow_UsesOnlyOneSerialRead(bool ttl)
    {
        var backend = new Backend { Version = 5 };
        using var table = await backend.CreateTable(ttl);
        var entry = Entry(SiloStatus.Active);
        var token = TestContext.Current.CancellationToken;

        Assert.False(await table.UpdateRowAsync(entry, "5", new TableVersion(6, "5"), token));

        AssertSingleRowRead(Assert.Single(backend.Executed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardedUpdate_RetiredAfterRead_StopsAfterFailedCasAndOneReread(bool ttl)
    {
        var backend = new Backend { Version = 5 };
        backend.Entries.Add(Entry(SiloStatus.Dead));
        backend.OnExecute = command =>
        {
            if (!command.Cql.StartsWith("BEGIN BATCH", StringComparison.Ordinal))
            {
                return null;
            }

            backend.Entries.Clear();
            return Task.FromResult<RowSet>(Rows.Applied(false));
        };
        using var table = await backend.CreateTable(ttl);
        var entry = Entry(SiloStatus.Dead);
        entry.IAmAliveTime = Timestamp.AddSeconds(1);
        var token = TestContext.Current.CancellationToken;

        Assert.False(await table.UpdateRowAsync(entry, "5", new TableVersion(6, "5"), token));

        Assert.Collection(backend.Executed,
            AssertSingleRowRead,
            command => Assert.StartsWith("BEGIN BATCH", command.Cql),
            AssertSingleRowRead);
        Assert.Empty(backend.Entries);
        Assert.Equal(5, backend.Version);
    }

    [Fact]
    public async Task GatewayQuery_UsesQuorum_WhileMembershipReadsRemainSerial()
    {
        var backend = new Backend();
        var queries = await OrleansQueries.CreateInstance(backend.Session);
        var token = TestContext.Current.CancellationToken;
        var gateway = await queries.GatewaysQuery("service-cluster", (int)SiloStatus.Active);
        Assert.Equal(ConsistencyLevel.Quorum, gateway.ConsistencyLevel);

        var version = await queries.MembershipReadVersion("service-cluster", token);
        var row = await queries.MembershipReadRow("service-cluster", Entry(SiloStatus.Active).SiloAddress, token);
        var all = await queries.MembershipReadAll("service-cluster", token);
        Assert.All(new[] { version, row, all }, statement => Assert.Equal(ConsistencyLevel.Serial, statement.ConsistencyLevel));
    }

    [Theory]
    [InlineData(false, SiloStatus.Active, 0)]
    [InlineData(true, SiloStatus.Active, 0)]
    [InlineData(false, SiloStatus.Dead, 0)]
    [InlineData(true, SiloStatus.Dead, 60)]
    public async Task MembershipWrites_AtomicallyFenceVersionAndRow_WithStatusSpecificTtl(bool ttl, SiloStatus status, int expectedTtl)
    {
        var backend = new Backend();
        var queries = await OrleansQueries.CreateInstance(backend.Session, ttl ? 60 : null);
        var entry = Entry(status);
        var insert = backend.Get(await queries.InsertMembership("service-cluster", entry, 4, TestContext.Current.CancellationToken));

        Assert.StartsWith("BEGIN BATCH UPDATE membership USING TTL 0 SET version = :new_version", insert.Cql);
        Assert.Contains("IF version = :expected_version;", insert.Cql);
        Assert.Contains("IF start_time = null; APPLY BATCH;", insert.Cql);
        Assert.Equal(4, insert.Values["expected_version"]);
        Assert.Equal(5, insert.Values["new_version"]);
        Assert.Equal(expectedTtl, insert.Values["ttl"]);
        AssertFullRow(insert, entry);
        Assert.Equal(ConsistencyLevel.Serial, insert.Statement.SerialConsistencyLevel);
        Assert.Equal(ConsistencyLevel.Quorum, insert.Statement.ConsistencyLevel);

        var update = backend.Get(await queries.UpdateMembership("service-cluster", entry, 4, Timestamp.AddSeconds(10), TestContext.Current.CancellationToken));
        Assert.StartsWith("BEGIN BATCH UPDATE membership USING TTL 0 SET version = :new_version", update.Cql);
        Assert.Contains("IF version = :expected_version;", update.Cql);
        Assert.Contains("IF i_am_alive_time = :previous_time AND start_time != null; APPLY BATCH;", update.Cql);
        Assert.Equal(expectedTtl, update.Values["ttl"]);
        Assert.Equal(Timestamp.AddSeconds(10), update.Values["i_am_alive_time"]);
        Assert.Equal(Timestamp.AddSeconds(10), update.Values["previous_time"]);
        Assert.Equal(5, update.Values["new_version"]);
        AssertFullRow(update, entry);

        var version = backend.Get(await queries.InsertMembershipVersion("service-cluster", TestContext.Current.CancellationToken));
        Assert.EndsWith("IF NOT EXISTS USING TTL 0;", version.Cql);
        Assert.Equal(ConsistencyLevel.Serial, version.Statement.SerialConsistencyLevel);
    }

    [Fact]
    public async Task Cleanup_CapturesEveryMutableField_WithoutWritingVersion()
    {
        var backend = new Backend();
        var queries = await OrleansQueries.CreateInstance(backend.Session);
        var entry = Entry(SiloStatus.Dead);
        var command = backend.Get(await queries.DeleteMembershipEntry("service-cluster", entry, TestContext.Current.CancellationToken));

        Assert.StartsWith("DELETE FROM membership WHERE partition_key", command.Cql);
        Assert.EndsWith("IF status = :status AND i_am_alive_time = :i_am_alive_time AND start_time = :start_time AND suspect_times = :suspect_times AND silo_name = :silo_name AND host_name = :host_name AND proxy_port = :proxy_port;", command.Cql);
        Assert.Equal((int)SiloStatus.Dead, command.Values["status"]);
        Assert.Equal(entry.StartTime, command.Values["start_time"]);
        Assert.Equal(entry.IAmAliveTime, command.Values["i_am_alive_time"]);
        Assert.Equal(entry.SiloName, command.Values["silo_name"]);
        Assert.Equal(entry.HostName, command.Values["host_name"]);
        Assert.Equal(entry.ProxyPort, command.Values["proxy_port"]);
        var vote = Assert.Single(entry.SuspectTimes!);
        Assert.Equal($"{vote.Item1.ToParsableString()},{LogFormatter.PrintDate(vote.Item2)}", command.Values["suspect_times"]);
        Assert.DoesNotContain("version", command.Cql);
        Assert.Equal(ConsistencyLevel.Serial, command.Statement.SerialConsistencyLevel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Cleanup_UsesExclusiveMaximumOfStartHeartbeatAndEveryVote(long cutoffTicks)
    {
        var backend = new Backend { Version = int.MaxValue };
        foreach (var status in Enum.GetValues<SiloStatus>())
        {
            var entry = Entry(status, backend.Entries.Count + 1);
            entry.StartTime = entry.IAmAliveTime = Timestamp.AddDays(-1);
            entry.SuspectTimes = [];
            backend.Entries.Add(entry);
        }

        for (var field = 0; field < 3; field++)
        {
            var entry = Entry(SiloStatus.Dead, backend.Entries.Count + 1);
            entry.StartTime = field == 0 ? Timestamp : Timestamp.AddDays(-1);
            entry.IAmAliveTime = field == 1 ? Timestamp : Timestamp.AddDays(-1);
            // The newest vote is deliberately first.
            entry.SuspectTimes = field == 2
                ? [Tuple.Create(entry.SiloAddress, Timestamp), Tuple.Create(entry.SiloAddress, Timestamp.AddDays(-1))]
                : null;
            backend.Entries.Add(entry);
        }

        using var table = await backend.CreateTable();
        await table.CleanupDefunctSiloEntriesAsync(new DateTimeOffset(Timestamp.AddTicks(cutoffTicks)), TestContext.Current.CancellationToken);

        var deletes = backend.Executed.Where(command => command.Cql.StartsWith("DELETE FROM", StringComparison.Ordinal)).ToList();
        Assert.Equal(cutoffTicks == 0 ? 1 : 4, deletes.Count);
        Assert.All(deletes, command =>
        {
            Assert.Equal((int)SiloStatus.Dead, command.Values["status"]);
            Assert.DoesNotContain("version", command.Cql);
        });
        Assert.Equal(int.MaxValue, backend.Version);
    }

    [Theory]
    [InlineData("invalid", 1)]
    [InlineData("01", 2)]
    [InlineData("0", 5)]
    [InlineData("-1", 0)]
    [InlineData("2147483647", int.MaxValue)]
    public async Task MembershipWrites_RejectInvalidTableTokensBeforeExecuting(string etag, int version)
    {
        var backend = new Backend();
        using var table = await backend.CreateTable();
        backend.Executed.Clear();
        var entry = Entry(SiloStatus.Active);
        var token = TestContext.Current.CancellationToken;

        Assert.False(await table.InsertRowAsync(entry, new TableVersion(version, etag), token));
        Assert.False(await table.UpdateRowAsync(entry, etag, new TableVersion(version, etag), token));
        Assert.Empty(backend.Executed);
    }

    [Fact]
    public async Task FullRowWrite_RetriesHeartbeatRace_PreservingMaximumTime()
    {
        var backend = new Backend { Version = 4 };
        var entry = Entry(SiloStatus.Active);
        backend.Entries.Add(entry);
        var writes = 0;
        backend.OnExecute = command =>
        {
            if (!command.Cql.StartsWith("BEGIN BATCH", StringComparison.Ordinal))
            {
                return null;
            }

            writes++;
            if (writes == 1)
            {
                entry.IAmAliveTime = Timestamp.AddMinutes(1);
                return Task.FromResult<RowSet>(Rows.Applied(false));
            }

            Assert.Equal(entry.IAmAliveTime, command.Values["previous_time"]);
            Assert.Equal(entry.IAmAliveTime, command.Values["i_am_alive_time"]);
            return Task.FromResult<RowSet>(Rows.Applied(true));
        };
        using var table = await backend.CreateTable();
        var staleEntry = Entry(SiloStatus.Dead);
        var token = TestContext.Current.CancellationToken;
        Assert.False(await table.UpdateRowAsync(staleEntry, "3", new TableVersion(5, "4"), token));
        Assert.True(await table.UpdateRowAsync(staleEntry, "4", new TableVersion(5, "4"), token));
        Assert.Equal(2, writes);
        Assert.Equal(Timestamp, staleEntry.IAmAliveTime);
        Assert.Collection(backend.Executed,
            AssertSingleRowRead,
            command => Assert.StartsWith("BEGIN BATCH", command.Cql),
            AssertSingleRowRead,
            command => Assert.StartsWith("BEGIN BATCH", command.Cql));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerHeartbeat_StorageFailurePropagatesAfterOneExecute(bool ttl)
    {
        var error = new InvalidOperationException("Cassandra operation failed.");
        var backend = new Backend { OnExecute = _ => Task.FromException<RowSet>(error) };
        using var table = await backend.CreateTable(ttl);
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(
            () => table.UpdateIAmAliveAsync(Entry(SiloStatus.Active), TestContext.Current.CancellationToken)));
        Assert.StartsWith("UPDATE membership", Assert.Single(backend.Executed).Cql);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerHeartbeat_CanceledDuringExecute_PreservesCancellationAndIssuesOneWrite(bool ttl)
    {
        var execution = new TaskCompletionSource<RowSet>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend { OnExecute = _ => execution.Task };
        using var table = await backend.CreateTable(ttl);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var heartbeat = table.UpdateIAmAliveAsync(Entry(SiloStatus.Active), cancellation.Token);
        Assert.False(heartbeat.IsCompleted);
        Assert.StartsWith("UPDATE membership", Assert.Single(backend.Executed).Cql);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => heartbeat);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Single(backend.Executed);
        Assert.False(execution.Task.IsCompleted);
        execution.SetResult(new Rows());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Heartbeat_PreCanceled_ExecutesNoStatements(bool ttl)
    {
        var backend = new Backend();
        using var table = await backend.CreateTable(ttl);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => table.UpdateIAmAliveAsync(Entry(SiloStatus.Active), cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(backend.Executed);
    }

    [Fact]
    public async Task ReadAll_RetriesWhenVersionChangesAcrossPages()
    {
        var backend = new Backend { Version = 1 };
        backend.Entries.Add(Entry(SiloStatus.Active));
        backend.Entries.Add(Entry(SiloStatus.Active, 2));
        var reads = 0;
        var fetched = 0;
        backend.OnExecute = command =>
        {
            if (!command.Cql.StartsWith("SELECT version, address", StringComparison.Ordinal))
            {
                return null;
            }

            if (++reads > 1)
            {
                return null;
            }

            var rows = new Rows(Backend.Member(1, Entry(SiloStatus.Joining)));
            rows.SetNextPage(() =>
            {
                fetched++;
                backend.Version = 2;
                return Task.FromResult<RowSet>(new Rows(Backend.Member(2, backend.Entries[1])));
            });
            return Task.FromResult<RowSet>(rows);
        };
        using var table = await backend.CreateTable();
        var result = await table.ReadAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, reads);
        Assert.Equal(1, fetched);
        Assert.Equal(new TableVersion(2, "2"), result.Version);
        Assert.Equal(2, result.Members.Count);
        Assert.All(result.Members, row =>
        {
            Assert.Equal(SiloStatus.Active, row.Item1.Status);
            Assert.Equal("2", row.Item2);
        });
        Assert.All(backend.Executed.Where(command => command.Cql.StartsWith("SELECT version", StringComparison.Ordinal)),
            command => Assert.Equal(ConsistencyLevel.Serial, command.Statement.ConsistencyLevel));
    }

    private static void AssertSingleRowRead(Command command)
    {
        Assert.StartsWith("SELECT version, address", command.Cql);
        Assert.Contains("AND address = :address AND port = :port AND generation = :generation;", command.Cql);
        Assert.Equal(ConsistencyLevel.Serial, command.Statement.ConsistencyLevel);
    }

    private static void AssertFullRow(Command command, MembershipEntry entry)
    {
        foreach (var field in new[] { "status", "suspect_times", "i_am_alive_time", "silo_name", "host_name", "proxy_port", "start_time" })
        {
            Assert.Contains($"{field} = :{field}", command.Cql);
            Assert.Contains(field, command.Values.Keys);
        }

        Assert.Equal((int)entry.Status, command.Values["status"]);
        Assert.Equal(entry.SiloName, command.Values["silo_name"]);
        Assert.Equal(entry.HostName, command.Values["host_name"]);
        Assert.Equal(entry.ProxyPort, command.Values["proxy_port"]);
        Assert.Equal(entry.StartTime, command.Values["start_time"]);
    }

    private static MembershipEntry Entry(SiloStatus status, int generation = 1) => new()
    {
        SiloAddress = SiloAddress.New(IPAddress.Loopback, 12345, generation),
        Status = status,
        HostName = "host",
        SiloName = "silo",
        ProxyPort = 30000,
        StartTime = Timestamp.AddDays(-1),
        IAmAliveTime = Timestamp,
        SuspectTimes = [Tuple.Create(SiloAddress.New(IPAddress.Loopback, 12346, 1), Timestamp)]
    };

    private sealed record Command(string Cql, Dictionary<string, object?> Values, IStatement Statement);

    private sealed class Backend
    {
        private readonly Dictionary<IStatement, Command> _commands = [];
        public ISession Session { get; } = Substitute.For<ISession>();
        public List<MembershipEntry> Entries { get; } = [];
        public List<Command> Executed { get; } = [];
        public int Version { get; set; }
        public Func<Command, Task<RowSet>?>? OnExecute { get; set; }

        public Backend()
        {
            Session.Keyspace.Returns("orleans");
            Session.PrepareAsync(Arg.Any<string>()).Returns(call =>
            {
                var cql = Regex.Replace(call.Arg<string>(), @"\s+", " ").Trim();
                var prepared = Substitute.For<PreparedStatement>();
                prepared.Bind(Arg.Any<object[]>()).Returns(binding =>
                {
                    var arguments = binding.Arg<object[]>();
                    var values = arguments.Length == 1 && arguments[0] is not string
                        ? arguments[0].GetType().GetProperties().ToDictionary(property => property.Name, property => property.GetValue(arguments[0]))
                        : new Dictionary<string, object?>();
                    var statement = new BoundStatement();
                    statement.SetConsistencyLevel(prepared.ConsistencyLevel!.Value);
                    _commands.Add(statement, new Command(cql, values, statement));
                    return statement;
                });
                return Task.FromResult(prepared);
            });
            Session.ExecuteAsync(Arg.Any<IStatement>()).Returns(call =>
            {
                var statement = call.Arg<IStatement>();
                if (statement is SimpleStatement simple)
                {
                    if (simple.QueryString.StartsWith("SELECT * FROM system_schema.tables", StringComparison.Ordinal))
                    {
                        return Task.FromResult<RowSet>(new Rows(new Row()));
                    }

                    throw new InvalidOperationException($"Unexpected simple statement: {simple.QueryString}");
                }

                var command = Get(statement);
                Executed.Add(command);
                if (OnExecute?.Invoke(command) is { } result)
                {
                    return result;
                }

                if (command.Cql.StartsWith("SELECT version FROM membership", StringComparison.Ordinal))
                {
                    return Task.FromResult<RowSet>(new Rows(CreateRow(new() { ["version"] = Version })));
                }

                if (command.Cql.StartsWith("SELECT version, address", StringComparison.Ordinal))
                {
                    return Task.FromResult<RowSet>(new Rows(Entries.Select(entry => Member(Version, entry)).ToArray()));
                }

                if (command.Cql.StartsWith("DELETE FROM", StringComparison.Ordinal))
                {
                    return Task.FromResult<RowSet>(Rows.Applied(true));
                }

                throw new InvalidOperationException($"Unexpected statement: {command.Cql}");
            });
        }

        public Command Get(IStatement statement) => _commands[statement];

        public async Task<CassandraClusteringTable> CreateTable(bool ttl = false)
        {
            var options = new CassandraClusteringOptions { UseCassandraTtl = ttl };
            options.ConfigureClient(_ => Task.FromResult(Session));
            var table = new CassandraClusteringTable(
                Options.Create(new ClusterOptions { ServiceId = "service", ClusterId = "cluster" }),
                Options.Create(options),
                Options.Create(new ClusterMembershipOptions { DefunctSiloExpiration = TimeSpan.FromSeconds(60) }),
                Substitute.For<IServiceProvider>());
            await table.InitializeMembershipTableAsync(false, TestContext.Current.CancellationToken);
            return table;
        }

        public static Row Member(int version, MembershipEntry entry) => CreateRow(new()
        {
            ["version"] = version,
            ["address"] = entry.SiloAddress.Endpoint.Address.ToString(),
            ["port"] = entry.SiloAddress.Endpoint.Port,
            ["generation"] = entry.SiloAddress.Generation,
            ["silo_name"] = entry.SiloName,
            ["host_name"] = entry.HostName,
            ["status"] = (int)entry.Status,
            ["proxy_port"] = entry.ProxyPort,
            ["start_time"] = new DateTimeOffset(entry.StartTime),
            ["i_am_alive_time"] = new DateTimeOffset(entry.IAmAliveTime),
            ["suspect_times"] = entry.SuspectTimes is null ? null : string.Join("|",
                entry.SuspectTimes.Select(vote => $"{vote.Item1.ToParsableString()},{LogFormatter.PrintDate(vote.Item2)}"))
        });
    }

    private static Row CreateRow(Dictionary<string, object?> values)
    {
        var columns = values.Select((value, index) => new CqlColumn
        {
            Name = value.Key,
            Index = index,
            Type = value.Value?.GetType() ?? typeof(object)
        }).ToArray();
        return (Row)typeof(Row).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(object[]), typeof(CqlColumn[]), typeof(Dictionary<string, int>)], null)!
            .Invoke([values.Values.ToArray(), columns, columns.ToDictionary(column => column.Name, column => column.Index)]);
    }

    private sealed class Rows : RowSet
    {
        public Rows(params Row[] rows)
        {
            foreach (var row in rows)
            {
                RowQueue.Enqueue(row);
            }
        }

        public static Rows Applied(bool applied) => new(CreateRow(new() { ["[applied]"] = applied }));

        public void SetNextPage(Func<Task<RowSet>> fetch)
        {
            typeof(RowSet).GetProperty(nameof(PagingState))!.SetValue(this, new byte[] { 1 });
            typeof(RowSet).GetMethod("SetFetchNextPageHandler", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(this, new object?[] { (Func<byte[], Task<RowSet>>)(_ => fetch()), 10000, null });
        }

        protected override void PageNext() => throw new InvalidOperationException("Page fetch must be awaited.");
    }
}
