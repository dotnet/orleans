using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Membership;
using Orleans.Runtime.MembershipService;
using Orleans.Tests.SqlUtils;
using UnitTests.General;

namespace UnitTests.StorageTests.Relational;

[TestSuite("Functional")]
[TestArea("Membership")]
public sealed class AdoNetMembershipUpgradeTests
{
    private const string ClusterId = "membership-compatibility";
    private static readonly DateTime StartTime = new(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    // src/AdoNet/Orleans.Clustering.AdoNet/*-Clustering.sql from d515d75eaaa3a0390a74cb11c96aa758358b5a67,
    // extracted with only trailing line spaces/tabs removed. Hashes also normalize checkout line endings.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [TestProvider("SqlServer")]
    public Task SqlServer_NativeMembership_OriginalAndEnhancedCatalogs(bool enhanced) =>
        VerifyInstallationAsync("SQLServer", AdoNetInvariants.InvariantNameSqlServer, enhanced,
            "FB94B7309CA2FDB11BDC8FDA44684DC2CA4636BDC570A491EBF91E9D4E579660");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [TestProvider("PostgreSql")]
    public Task PostgreSql_NativeMembership_OriginalAndEnhancedCatalogs(bool enhanced) =>
        VerifyInstallationAsync("PostgreSQL", AdoNetInvariants.InvariantNamePostgreSql, enhanced,
            "75682538351DC15AD74D86FAB14110CCA2603FD063D19CEBC76118FF84C88328");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [TestProvider("MySql")]
    public Task MySql_NativeMembership_OriginalAndEnhancedCatalogs(bool enhanced) =>
        VerifyInstallationAsync("MySQL", AdoNetInvariants.InvariantNameMySql, enhanced,
            "FD33B6AD41DDA95942F275857F27C7FD02A0DB433747DA4C3B92D677ACB6014E");

    private static async Task VerifyInstallationAsync(string engine, string invariant, bool enhanced, string fixtureHash)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "MembershipUpgradeFixtures", $"{engine}-Clustering.sql");
        var fixture = await File.ReadAllTextAsync(fixturePath, cancellationToken);
        Assert.Equal(fixtureHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Replace("\r\n", "\n")))));
        var clusteringPath = enhanced ? Path.Combine(AppContext.BaseDirectory, $"{engine}-Clustering.sql") : fixturePath;
        var database = await RelationalStorageForTesting.SetupInstance(
            invariant,
            $"MembershipCompatibility_{Guid.NewGuid():N}",
            cancellationToken: cancellationToken,
            setupSqlScriptFileNames: [Path.Combine(AppContext.BaseDirectory, $"{engine}-Main.sql"), clusteringPath]);
        var storage = database.Storage;
        var queries = await ReadQueriesAsync(storage, cancellationToken);
        Assert.Equal(enhanced, queries.ContainsKey("CleanupDefunctSiloEntryKey"));
        Assert.Equal(enhanced, queries["InsertMembershipKey"].Contains("@SuspectTimes", StringComparison.Ordinal));
        var connectionString = database.CurrentConnectionString;
        if (enhanced && engine == "PostgreSQL")
        {
            // Enhanced rollback must not depend on the server's optional PL/pgSQL assertions.
            connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Options = "-c plpgsql.check_asserts=off" }.ConnectionString;
        }

        using var services = new ServiceCollection().BuildServiceProvider();
        var clusterOptions = Options.Create(new ClusterOptions { ClusterId = ClusterId });
        var current = new AdoNetClusteringTable(
            services, clusterOptions,
            Options.Create(new AdoNetClusteringSiloOptions { Invariant = invariant, ConnectionString = connectionString }),
            NullLogger<AdoNetClusteringTable>.Instance);
        var gateway = new AdoNetGatewayListProvider(
            NullLogger<AdoNetGatewayListProvider>.Instance, services,
            Options.Create(new AdoNetClusteringClientOptions { Invariant = invariant, ConnectionString = connectionString }),
            Options.Create(new GatewayOptions()), clusterOptions);
        await current.InitializeMembershipTableAsync(true, cancellationToken);
        var empty = await ReadSnapshotAsync(storage, cancellationToken);
        AssertStored(empty, 0);
        AssertReadMatches(empty, await current.ReadAllAsync(cancellationToken));

        var cutoff = StartTime.AddHours(1);
        var active = Entry(1, SiloStatus.Active);
        active.ProxyPort = 30001;
        var dead = Entry(2, SiloStatus.Dead);
        var suspected = Entry(3, SiloStatus.Dead);
        var joining = Entry(4, SiloStatus.Joining);
        suspected.SuspectTimes =
        [
            Tuple.Create(active.SiloAddress, StartTime.AddMinutes(1)),
            Tuple.Create(joining.SiloAddress, StartTime.AddMinutes(2))
        ];
        var startingAtCutoff = Entry(5, SiloStatus.Dead);
        startingAtCutoff.StartTime = cutoff;
        var heartbeatAtCutoff = Entry(6, SiloStatus.Dead);
        heartbeatAtCutoff.IAmAliveTime = cutoff;
        var oldSuspect = Entry(7, SiloStatus.Dead);
        var entries = new[] { active, dead, suspected, joining, startingAtCutoff, heartbeatAtCutoff, oldSuspect };
        for (var version = 0; version < entries.Length; version++)
        {
            Assert.True(await current.InsertRowAsync(entries[version], new TableVersion(version + 1, version.ToString(CultureInfo.InvariantCulture)), cancellationToken));
        }

        var inserted = await ReadSnapshotAsync(storage, cancellationToken);
        Assert.Equal(entries.Length, inserted.Version);
        var expectedInserted = StoredMember.FromEntry(suspected);
        if (!enhanced)
        {
            // The original catalog accepts the unused input parameter but its insert does not store votes.
            expectedInserted = expectedInserted with { SuspectTimes = null };
        }

        Assert.Equal(expectedInserted, Assert.Single(inserted.Members, row => row.SiloAddress.Equals(suspected.SiloAddress)));
        var insertedRow = Assert.Single((await current.ReadRowAsync(suspected.SiloAddress, cancellationToken)).Members).Item1;
        Assert.Equal(expectedInserted, StoredMember.FromEntry(insertedRow));
        if (enhanced)
        {
            Assert.Equal(suspected.SuspectTimes, insertedRow.SuspectTimes);
        }
        else
        {
            Assert.Null(insertedRow.SuspectTimes);
        }

        AssertReadMatches(inserted, await current.ReadAllAsync(cancellationToken));

        suspected.SuspectTimes =
        [
            Tuple.Create(active.SiloAddress, cutoff),
            Tuple.Create(joining.SiloAddress, cutoff.AddSeconds(-1))
        ];
        oldSuspect.SuspectTimes = [Tuple.Create(active.SiloAddress, cutoff.AddSeconds(-1))];
        foreach (var entry in new[] { suspected, oldSuspect })
        {
            var row = await current.ReadRowAsync(entry.SiloAddress, cancellationToken);
            Assert.True(await current.UpdateRowAsync(entry, Assert.Single(row.Members).Item2, row.Version.Next(), cancellationToken));
            var updatedRow = Assert.Single((await current.ReadRowAsync(entry.SiloAddress, cancellationToken)).Members).Item1;
            Assert.Equal(entry.SuspectTimes, updatedRow.SuspectTimes);
        }

        // A populated original installation supports package upgrades without any catalog edits.
        var populated = await ReadSnapshotAsync(storage, cancellationToken);
        AssertStored(populated, 9, entries);
        await current.InitializeMembershipTableAsync(true, cancellationToken);
        await gateway.InitializeGatewayListProvider().WaitAsync(cancellationToken);
        AssertReadMatches(populated, await current.ReadAllAsync(cancellationToken));
        Assert.Equal(
            [SiloAddress.New(active.SiloAddress.Endpoint.Address, active.ProxyPort, active.SiloAddress.Generation).ToGatewayUri()],
            await gateway.GetGateways().WaitAsync(cancellationToken));
        AssertUnchanged(populated, await ReadSnapshotAsync(storage, cancellationToken));
        Assert.Equal(queries.OrderBy(pair => pair.Key), (await ReadQueriesAsync(storage, cancellationToken)).OrderBy(pair => pair.Key));

        var captured = await current.ReadRowAsync(active.SiloAddress, cancellationToken);
        Assert.Equal(populated.Version, captured.Version.Version);
        Assert.Equal(populated.Etag, captured.Version.VersionEtag);
        var capturedRow = Assert.Single(captured.Members);
        Assert.Equal(StoredMember.FromEntry(active), StoredMember.FromEntry(capturedRow.Item1));
        var originalRowToken = capturedRow.Item2;
        Assert.Equal(populated.Etag, originalRowToken);
        active.IAmAliveTime = StartTime.AddMinutes(10);
        await current.UpdateIAmAliveAsync(active, cancellationToken);
        var afterHeartbeat = await ReadSnapshotAsync(storage, cancellationToken);
        AssertStored(afterHeartbeat, 9, entries);
        Assert.Equal(populated.VersionTimestamp, afterHeartbeat.VersionTimestamp);
        active.Status = SiloStatus.ShuttingDown;
        active.SuspectTimes = [Tuple.Create(joining.SiloAddress, StartTime.AddMinutes(4))];
        active.IAmAliveTime = StartTime.AddMinutes(5);
        Assert.True(await current.UpdateRowAsync(active, originalRowToken, captured.Version.Next(), cancellationToken));
        // Original SQL can overwrite a newer heartbeat; only enhanced full-row writes retain MAX.
        active.IAmAliveTime = StartTime.AddMinutes(enhanced ? 10 : 5);
        var afterUpdate = await ReadSnapshotAsync(storage, cancellationToken);
        AssertStored(afterUpdate, 10, entries);
        active.IAmAliveTime = StartTime.AddMinutes(3);
        await current.UpdateIAmAliveAsync(active, cancellationToken);
        var afterBlindHeartbeat = await ReadSnapshotAsync(storage, cancellationToken);
        AssertStored(afterBlindHeartbeat, 10, entries);
        Assert.Equal(afterUpdate.VersionTimestamp, afterBlindHeartbeat.VersionTimestamp);
        active.IAmAliveTime = StartTime.AddMinutes(20);
        await current.UpdateIAmAliveAsync(active, cancellationToken);
        var stable = await ReadSnapshotAsync(storage, cancellationToken);
        AssertStored(stable, 10, entries);
        Assert.Equal(afterUpdate.VersionTimestamp, stable.VersionTimestamp);
        await current.UpdateIAmAliveAsync(Entry(20, SiloStatus.Active), cancellationToken);
        AssertUnchanged(stable, await ReadSnapshotAsync(storage, cancellationToken));

        if (enhanced)
        {
            var absent = Entry(20, SiloStatus.Dead);
            var changed = Entry(1, SiloStatus.Dead);
            changed.IAmAliveTime = StartTime.AddDays(1);
            changed.SuspectTimes = [Tuple.Create(joining.SiloAddress, StartTime.AddDays(1))];
            await AssertRejectedAsync(() => current.InsertRowAsync(absent, captured.Version.Next(), cancellationToken));
            await AssertRejectedAsync(() => current.UpdateRowAsync(changed, originalRowToken, captured.Version.Next(), cancellationToken));
            await AssertRejectedAsync(() => current.InsertRowAsync(changed, stable.NextVersion, cancellationToken));
            await AssertRejectedAsync(() => current.UpdateRowAsync(changed, originalRowToken, stable.NextVersion, cancellationToken));
            await AssertRejectedAsync(() => current.UpdateRowAsync(absent, stable.Etag, stable.NextVersion, cancellationToken));

            absent.HostName = null!;
            var error = await Assert.ThrowsAnyAsync<DbException>(() => current.InsertRowAsync(absent, stable.NextVersion, cancellationToken));
            Assert.True(engine switch
            {
                "SQLServer" => error is Microsoft.Data.SqlClient.SqlException { Number: 515 },
                "MySQL" => error is MySql.Data.MySqlClient.MySqlException { Number: 1048 },
                "PostgreSQL" => error is PostgresException { SqlState: PostgresErrorCodes.NotNullViolation },
                _ => false
            }, error.ToString());
            AssertUnchanged(stable, await ReadSnapshotAsync(storage, cancellationToken));
            using var canceled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => current.InsertRowAsync(Entry(20, SiloStatus.Joining), stable.NextVersion, canceled.Token));
            AssertUnchanged(stable, await ReadSnapshotAsync(storage, cancellationToken));
        }

        // Original cleanup retains its original semantics, so use a safe no-op cutoff in that mode.
        await current.CleanupDefunctSiloEntriesAsync(new DateTimeOffset(enhanced ? cutoff : StartTime), cancellationToken);
        var afterCleanup = await ReadSnapshotAsync(storage, cancellationToken);
        var survivors = enhanced ? entries.Where(entry => entry != dead && entry != oldSuspect).ToArray() : entries;
        AssertStored(afterCleanup, 10, survivors);
        Assert.Equal(stable.VersionTimestamp, afterCleanup.VersionTimestamp);
        AssertReadMatches(afterCleanup, await current.ReadAllAsync(cancellationToken));

        if (engine == "SQLServer")
        {
            Assert.True(Assert.Single(await storage.ReadAsync(
                "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()",
                record => record.GetBoolean(0), null, cancellationToken)));
            var overlappingEntry = Entry(12, SiloStatus.Joining);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var insertion = InsertAfterBarrierAsync();
            var reading = ReadAfterBarrierAsync();
            start.SetResult();
            await Task.WhenAll(insertion, reading);
            Assert.True(await insertion);
            var observed = await reading;
            var afterOverlap = await ReadSnapshotAsync(storage, cancellationToken);
            AssertStored(afterOverlap, 11, survivors.Append(overlappingEntry).ToArray());
            Assert.Equal(observed.Version.Version.ToString(CultureInfo.InvariantCulture), observed.Version.VersionEtag);
            if (observed.Version.Version == afterCleanup.Version)
            {
                Assert.Empty(observed.Members);
            }
            else
            {
                Assert.Equal(afterOverlap.Version, observed.Version.Version);
                var row = Assert.Single(observed.Members);
                Assert.Equal(afterOverlap.Etag, row.Item2);
                Assert.Equal(StoredMember.FromEntry(overlappingEntry), StoredMember.FromEntry(row.Item1));
            }

            async Task<bool> InsertAfterBarrierAsync()
            {
                await start.Task.WaitAsync(cancellationToken);
                return await current.InsertRowAsync(overlappingEntry, afterCleanup.NextVersion, cancellationToken);
            }

            async Task<MembershipTableData> ReadAfterBarrierAsync()
            {
                await start.Task.WaitAsync(cancellationToken);
                return await current.ReadRowAsync(overlappingEntry.SiloAddress, cancellationToken);
            }
        }

        Assert.Equal(queries.OrderBy(pair => pair.Key), (await ReadQueriesAsync(storage, cancellationToken)).OrderBy(pair => pair.Key));
        async Task AssertRejectedAsync(Func<Task<bool>> write)
        {
            Assert.False(await write());
            AssertUnchanged(stable, await ReadSnapshotAsync(storage, cancellationToken));
        }
    }

    private static MembershipEntry Entry(int id, SiloStatus status) => new()
    {
        SiloAddress = SiloAddress.New(IPAddress.Loopback, 11000 + id, 100 + id),
        SiloName = $"silo-{id}",
        HostName = $"host-{id}",
        Status = status,
        StartTime = StartTime,
        IAmAliveTime = StartTime
    };

    private static async Task<Dictionary<string, string>> ReadQueriesAsync(IRelationalStorage storage, CancellationToken cancellationToken) =>
        (await storage.ReadAsync(DbStoredQueries.GetQueriesKey, DbStoredQueries.Converters.GetQueryKeyAndValue, null, cancellationToken))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

    private static async Task<Snapshot> ReadSnapshotAsync(IRelationalStorage storage, CancellationToken cancellationToken)
    {
        var version = Assert.Single(await storage.ReadAsync(
            "SELECT Version, Timestamp FROM OrleansMembershipVersionTable WHERE DeploymentId = @DeploymentId",
            record => (Version: record.GetInt32(0), Timestamp: record.GetDateTime(1)),
            command => _ = new DbStoredQueries.Columns(command) { DeploymentId = ClusterId }, cancellationToken));
        var members = await storage.ReadAsync(
            """
            SELECT DeploymentId, Address, Port, Generation, SiloName, HostName, Status, ProxyPort, SuspectTimes, StartTime, IAmAliveTime
            FROM OrleansMembershipTable WHERE DeploymentId = @DeploymentId ORDER BY Address, Port, Generation
            """,
            record => new StoredMember(
                record.GetString(0),
                SiloAddress.New(IPAddress.Parse(record.GetString(1)), record.GetInt32(2), record.GetInt32(3)),
                record.GetString(4), record.GetString(5), (SiloStatus)record.GetInt32(6), record.GetInt32(7),
                record.IsDBNull(8) ? null : record.GetString(8), record.GetDateTime(9), record.GetDateTime(10)),
            command => _ = new DbStoredQueries.Columns(command) { DeploymentId = ClusterId }, cancellationToken);
        return new Snapshot(version.Version, version.Timestamp, members.ToArray());
    }

    private static void AssertStored(Snapshot snapshot, int version, params MembershipEntry[] entries)
    {
        Assert.Equal(version, snapshot.Version);
        Assert.Equal(entries.OrderBy(entry => entry.SiloAddress.Endpoint.Port).Select(StoredMember.FromEntry), snapshot.Members);
    }

    private static void AssertUnchanged(Snapshot before, Snapshot after)
    {
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.VersionTimestamp, after.VersionTimestamp);
        Assert.Equal(before.Members, after.Members);
    }

    private static void AssertReadMatches(Snapshot expected, MembershipTableData actual)
    {
        Assert.Equal(expected.Version, actual.Version.Version);
        Assert.Equal(expected.Etag, actual.Version.VersionEtag);
        Assert.All(actual.Members, member => Assert.Equal(expected.Etag, member.Item2));
        Assert.Equal(expected.Members, actual.Members.OrderBy(member => member.Item1.SiloAddress.Endpoint.Port).Select(member => StoredMember.FromEntry(member.Item1)));
    }

    private sealed record Snapshot(int Version, DateTime VersionTimestamp, StoredMember[] Members)
    {
        public string Etag => Version.ToString(CultureInfo.InvariantCulture);
        public TableVersion NextVersion => new(Version + 1, Etag);
    }

    private sealed record StoredMember(
        string DeploymentId, SiloAddress SiloAddress, string SiloName, string HostName, SiloStatus Status,
        int ProxyPort, string? SuspectTimes, DateTime StartTime, DateTime IAmAliveTime)
    {
        public static StoredMember FromEntry(MembershipEntry entry) => new(
            ClusterId, entry.SiloAddress, entry.SiloName, entry.HostName, entry.Status, entry.ProxyPort,
            entry.SuspectTimes is null ? null : string.Join("|", entry.SuspectTimes.Select(vote => $"{vote.Item1.ToParsableString()},{LogFormatter.PrintDate(vote.Item2)}")),
            entry.StartTime, entry.IAmAliveTime);
    }
}
