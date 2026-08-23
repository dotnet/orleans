using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Clustering.EntityFrameworkCore.Data;
using Orleans.Configuration;
using Orleans.Runtime;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.Clustering;

[TestArea("EFCore")]
[TestProvider("None")]
[TestSuite("BVT")]
public sealed class EFMembershipTableRegressionTests
{
    [Fact]
    public async Task PR8654_Membership_ReadRow_AbsentAddressReturnsCurrentVersionAndNoMember()
    {
        await using var fixture = await SqliteMembershipRegressionFixture.Create();
        var existing = CreateMembershipEntry("192.0.2.41", 24101, 4101, "existing");

        var initial = await fixture.Table.ReadAll();
        Assert.True(await fixture.Table.InsertRow(existing, initial.Version.Next()));
        var afterInsert = await fixture.Table.ReadAll();
        var inserted = Assert.Single(afterInsert.Members);

        existing.Status = SiloStatus.Active;
        existing.SiloName = "existing-updated";
        existing.IAmAliveTime = existing.IAmAliveTime.AddMinutes(3);
        Assert.True(await fixture.Table.UpdateRow(existing, inserted.Item2, afterInsert.Version.Next()));
        var current = await fixture.Table.ReadAll();
        Assert.Equal(2, current.Version.Version);
        Assert.False(string.IsNullOrWhiteSpace(current.Version.VersionEtag));

        var absentAddress = SiloAddress.New(new IPEndPoint(IPAddress.Parse("192.0.2.99"), 24999), 4999);
        var result = await fixture.Table.ReadRow(absentAddress);

        Assert.NotNull(result);
        Assert.Empty(result.Members);
        Assert.Equal(current.Version.Version, result.Version.Version);
        Assert.Equal(current.Version.VersionEtag, result.Version.VersionEtag);

        var unchanged = await fixture.Table.ReadAll();
        AssertSnapshot(current, unchanged);
    }

    [Fact]
    public async Task PR8654_Membership_ReadAll_CallerSplitQueryReturnsOneAtomicSnapshot()
    {
        await using var fixture = await SqliteMembershipRegressionFixture.Create();
        var memberA = CreateMembershipEntry("192.0.2.51", 25101, 5101, "snapshot-a");
        var initial = await fixture.Table.ReadAll();
        Assert.True(await fixture.Table.InsertRow(memberA, initial.Version.Next()));
        var snapshotA = await fixture.Table.ReadAll();
        Assert.Equal(1, snapshotA.Version.Version);
        AssertMembership(memberA, Assert.Single(snapshotA.Members).Item1);

        var memberB = CreateMembershipEntry("192.0.2.52", 25202, 5202, "snapshot-b");
        fixture.Interceptor.Arm();
        using var deadlockTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var writer = fixture.ReplaceMembershipAfterReaderOpens(memberB, deadlockTimeout.Token);

        var observed = await fixture.Table.ReadAll().WaitAsync(deadlockTimeout.Token);
        var snapshotB = await writer.WaitAsync(deadlockTimeout.Token);

        Assert.True(fixture.Interceptor.BarrierReached);
        Assert.Equal(1, fixture.Interceptor.BarrierHitCount);
        AssertCommandShape(fixture.Interceptor.Commands);

        AssertSnapshot(snapshotA, observed);
        Assert.DoesNotContain(
            observed.Members,
            member => member.Item1.SiloAddress.Equals(memberB.SiloAddress));
        Assert.NotEqual(snapshotB.Version.VersionEtag, observed.Version.VersionEtag);
        Assert.Equal(memberB.SiloAddress, Assert.Single(snapshotB.Members).Item1.SiloAddress);
    }

    private static MembershipEntry CreateMembershipEntry(
        string address,
        int port,
        int generation,
        string identity)
    {
        var start = new DateTime(2025, 8, 9, 10, 11, 12, DateTimeKind.Utc);
        return new MembershipEntry
        {
            SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Parse(address), port), generation),
            HostName = $"{identity}.example",
            SiloName = identity,
            Status = SiloStatus.Joining,
            ProxyPort = port + 1000,
            StartTime = start,
            IAmAliveTime = start.AddMinutes(1)
        };
    }

    private static void AssertSnapshot(MembershipTableData expected, MembershipTableData actual)
    {
        Assert.Equal(expected.Version.Version, actual.Version.Version);
        Assert.Equal(expected.Version.VersionEtag, actual.Version.VersionEtag);

        var expectedMember = Assert.Single(expected.Members);
        var actualMember = Assert.Single(actual.Members);
        Assert.Equal(expectedMember.Item2, actualMember.Item2);
        AssertMembership(expectedMember.Item1, actualMember.Item1);
    }

    private static void AssertMembership(MembershipEntry expected, MembershipEntry actual)
    {
        Assert.Equal(expected.SiloAddress, actual.SiloAddress);
        Assert.Equal(expected.HostName, actual.HostName);
        Assert.Equal(expected.SiloName, actual.SiloName);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.ProxyPort, actual.ProxyPort);
        Assert.Equal(expected.StartTime, actual.StartTime);
        Assert.Equal(expected.IAmAliveTime, actual.IAmAliveTime);
        Assert.Equal(expected.SuspectTimes, actual.SuspectTimes);
    }

    private static void AssertCommandShape(IReadOnlyList<string> commands)
    {
        Assert.Contains("\"Clusters\"", commands[0], StringComparison.Ordinal);
        if (commands.Count == 1)
        {
            Assert.Contains("\"Silos\"", commands[0], StringComparison.Ordinal);
            Assert.Contains("JOIN", commands[0], StringComparison.OrdinalIgnoreCase);
            return;
        }

        Assert.Equal(2, commands.Count);
        Assert.DoesNotContain("\"Silos\"", commands[0], StringComparison.Ordinal);
        Assert.Contains("\"Silos\"", commands[1], StringComparison.Ordinal);
    }

    private sealed class SqliteMembershipRegressionFixture : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly DbContextOptions<TestClusterDbContext> _writerOptions;
        private readonly EFMembershipTable<TestClusterDbContext, Guid> _writerTable;

        private SqliteMembershipRegressionFixture(
            string databasePath,
            DbContextOptions<TestClusterDbContext> writerOptions,
            TestClusterDbContextFactory readerFactory,
            string clusterId,
            SplitQueryBarrierInterceptor interceptor)
        {
            _databasePath = databasePath;
            _writerOptions = writerOptions;
            Interceptor = interceptor;
            Table = CreateTable(readerFactory, clusterId);
            _writerTable = CreateTable(new TestClusterDbContextFactory(writerOptions), clusterId);
        }

        public EFMembershipTable<TestClusterDbContext, Guid> Table { get; }

        public SplitQueryBarrierInterceptor Interceptor { get; }

        public static async Task<SqliteMembershipRegressionFixture> Create()
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"orleans-ef-membership-{Guid.NewGuid():N}.sqlite");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();
            var interceptor = new SplitQueryBarrierInterceptor();
            var readerOptions = new DbContextOptionsBuilder<TestClusterDbContext>()
                .UseSqlite(
                    connectionString,
                    sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .AddInterceptors(interceptor)
                .Options;
            var writerOptions = new DbContextOptionsBuilder<TestClusterDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var fixture = new SqliteMembershipRegressionFixture(
                databasePath,
                writerOptions,
                new TestClusterDbContextFactory(readerOptions),
                $"membership-regression-{Guid.NewGuid():N}",
                interceptor);

            await using (var context = new TestClusterDbContext(writerOptions))
            {
                await context.Database.EnsureCreatedAsync();
                await context.Database.OpenConnectionAsync();
                await using var command = context.Database.GetDbConnection().CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL;";
                var journalMode = (string?)await command.ExecuteScalarAsync();
                if (!string.Equals("wal", journalMode, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"SQLite WAL mode was not enabled: '{journalMode}'.");
                }

                command.CommandText = "PRAGMA busy_timeout=15000;";
                await command.ExecuteNonQueryAsync();
            }

            await fixture.Table.InitializeMembershipTable(tryInitTableVersion: true);
            return fixture;
        }

        public async Task<MembershipTableData> ReplaceMembershipAfterReaderOpens(
            MembershipEntry replacement,
            CancellationToken cancellationToken)
        {
            await Interceptor.FirstReaderOpened.WaitAsync(cancellationToken);
            try
            {
                await using var context = new TestClusterDbContext(_writerOptions);
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
                var cluster = await context.Clusters.SingleAsync(cancellationToken);
                var existing = await context.Silos.ToListAsync(cancellationToken);
                context.Silos.RemoveRange(existing);
                context.Silos.Add(ToRecord(cluster.Id, replacement));
                cluster.Version++;
                cluster.Timestamp = cluster.Timestamp.AddMinutes(1);
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return await _writerTable.ReadAll();
            }
            finally
            {
                Interceptor.SignalWriterCompleted();
            }
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            DeleteIfExists(_databasePath);
            DeleteIfExists($"{_databasePath}-wal");
            DeleteIfExists($"{_databasePath}-shm");
            return ValueTask.CompletedTask;
        }

        private static EFMembershipTable<TestClusterDbContext, Guid> CreateTable(
            IDbContextFactory<TestClusterDbContext> factory,
            string clusterId) =>
            new(
                NullLoggerFactory.Instance,
                Options.Create(new ClusterOptions { ClusterId = clusterId }),
                factory,
                new GuidClusterETagConverter());

        private static SiloRecord<Guid> ToRecord(string clusterId, MembershipEntry entry) =>
            new()
            {
                ClusterId = clusterId,
                Address = entry.SiloAddress.Endpoint.Address.ToString(),
                Port = entry.SiloAddress.Endpoint.Port,
                Generation = entry.SiloAddress.Generation,
                Name = entry.SiloName,
                HostName = entry.HostName,
                Status = entry.Status,
                ProxyPort = entry.ProxyPort,
                StartTime = entry.StartTime,
                IAmAliveTime = entry.IAmAliveTime
            };

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class TestClusterDbContextFactory(DbContextOptions<TestClusterDbContext> options)
        : IDbContextFactory<TestClusterDbContext>
    {
        public TestClusterDbContext CreateDbContext() => new(options);
    }

    private sealed class TestClusterDbContext(DbContextOptions<TestClusterDbContext> options)
        : GuidClusterDbContext<TestClusterDbContext>(options);

    private sealed class SplitQueryBarrierInterceptor : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _commands = new();
        private readonly TaskCompletionSource<bool> _firstReaderOpened =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _writerCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _barrierHitCount;

        public Task FirstReaderOpened => _firstReaderOpened.Task;

        public IReadOnlyList<string> Commands => _commands.ToArray();

        public bool BarrierReached => _firstReaderOpened.Task.IsCompletedSuccessfully;

        public int BarrierHitCount => Volatile.Read(ref _barrierHitCount);

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void SignalWriterCompleted() => _writerCompleted.TrySetResult(true);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 0 ||
                !command.CommandText.Contains("\"Clusters\"", StringComparison.Ordinal))
            {
                return result;
            }

            _commands.Enqueue(command.CommandText);
            if (Interlocked.CompareExchange(ref _barrierHitCount, 1, 0) == 0)
            {
                var hasFirstRow = await result.ReadAsync(cancellationToken);
                result = new ReplayFirstReadDbDataReader(result, hasFirstRow);
                _firstReaderOpened.TrySetResult(true);
                await _writerCompleted.Task.WaitAsync(cancellationToken);
            }

            return result;
        }

        private sealed class ReplayFirstReadDbDataReader(DbDataReader inner, bool hasFirstRow)
            : DbDataReader
        {
            private bool _replayFirstRead = hasFirstRow;

            public override object this[int ordinal] => inner[ordinal];

            public override object this[string name] => inner[name];

            public override int Depth => inner.Depth;

            public override int FieldCount => inner.FieldCount;

            public override bool HasRows => inner.HasRows;

            public override bool IsClosed => inner.IsClosed;

            public override int RecordsAffected => inner.RecordsAffected;

            public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);

            public override byte GetByte(int ordinal) => inner.GetByte(ordinal);

            public override long GetBytes(
                int ordinal,
                long dataOffset,
                byte[]? buffer,
                int bufferOffset,
                int length) =>
                inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

            public override char GetChar(int ordinal) => inner.GetChar(ordinal);

            public override long GetChars(
                int ordinal,
                long dataOffset,
                char[]? buffer,
                int bufferOffset,
                int length) =>
                inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);

            public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);

            public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);

            public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);

            public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);

            public override System.Collections.IEnumerator GetEnumerator() => inner.GetEnumerator();

            public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);

            public override T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(ordinal);

            public override Task<T> GetFieldValueAsync<T>(
                int ordinal,
                CancellationToken cancellationToken) =>
                inner.GetFieldValueAsync<T>(ordinal, cancellationToken);

            public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);

            public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);

            public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);

            public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);

            public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);

            public override string GetName(int ordinal) => inner.GetName(ordinal);

            public override int GetOrdinal(string name) => inner.GetOrdinal(name);

            public override string GetString(int ordinal) => inner.GetString(ordinal);

            public override object GetValue(int ordinal) => inner.GetValue(ordinal);

            public override int GetValues(object[] values) => inner.GetValues(values);

            public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);

            public override Task<bool> IsDBNullAsync(
                int ordinal,
                CancellationToken cancellationToken) =>
                inner.IsDBNullAsync(ordinal, cancellationToken);

            public override bool NextResult() => inner.NextResult();

            public override Task<bool> NextResultAsync(CancellationToken cancellationToken) =>
                inner.NextResultAsync(cancellationToken);

            public override bool Read()
            {
                if (_replayFirstRead)
                {
                    _replayFirstRead = false;
                    return true;
                }

                return inner.Read();
            }

            public override Task<bool> ReadAsync(CancellationToken cancellationToken)
            {
                if (_replayFirstRead)
                {
                    _replayFirstRead = false;
                    return Task.FromResult(true);
                }

                return inner.ReadAsync(cancellationToken);
            }

            public override void Close() => inner.Close();

            public override ValueTask DisposeAsync() => inner.DisposeAsync();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
