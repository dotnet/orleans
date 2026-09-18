using System.Data;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
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
    private const string ClusterId = "membership-upgrade";
    private static readonly DateTime StartTime = new(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    // src/AdoNet/Orleans.Clustering.AdoNet/*-Clustering.sql from d515d75eaaa3a0390a74cb11c96aa758358b5a67,
    // extracted with only trailing line spaces/tabs removed. Hashes also normalize checkout line endings.
    [Fact]
    [TestProvider("SqlServer")]
    public Task SqlServer_NativeUpgrade_PreservesCachedLegacyCaller() =>
        UpgradeAsync("SQLServer", AdoNetInvariants.InvariantNameSqlServer,
            "FB94B7309CA2FDB11BDC8FDA44684DC2CA4636BDC570A491EBF91E9D4E579660");

    [Fact]
    [TestProvider("PostgreSql")]
    public Task PostgreSql_NativeUpgrade_PreservesCachedLegacyCaller() =>
        UpgradeAsync("PostgreSQL", AdoNetInvariants.InvariantNamePostgreSql,
            "75682538351DC15AD74D86FAB14110CCA2603FD063D19CEBC76118FF84C88328");

    [Fact]
    [TestProvider("MySql")]
    public Task MySql_NativeUpgrade_PreservesCachedLegacyCaller() =>
        UpgradeAsync("MySQL", AdoNetInvariants.InvariantNameMySql,
            "FD33B6AD41DDA95942F275857F27C7FD02A0DB433747DA4C3B92D677ACB6014E");

    private static async Task UpgradeAsync(string engine, string invariant, string expectedFixtureHash)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "MembershipUpgradeFixtures", $"{engine}-Clustering.sql");
        var fixture = await File.ReadAllTextAsync(fixturePath, cancellationToken);
        Assert.Equal(expectedFixtureHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Replace("\r\n", "\n")))));

        // Do not install current clustering or glob migrations before exercising the frozen catalog.
        var database = await RelationalStorageForTesting.SetupInstance(
            invariant,
            $"MembershipUpgrade_{Guid.NewGuid():N}",
            cancellationToken: cancellationToken,
            setupSqlScriptFileNames: [Path.Combine(AppContext.BaseDirectory, $"{engine}-Main.sql"), fixturePath]);
        var storage = database.Storage;
        var queries = await ReadQueriesAsync(storage, cancellationToken);
        Assert.DoesNotContain("CleanupDefunctSiloEntryKey", queries.Keys);
        var legacy = new CachedLegacyClient(storage, queries, cancellationToken);
        Assert.True(await legacy.InsertVersionAsync());

        var active = Entry(1, SiloStatus.Active);
        active.ProxyPort = 30001;
        var dead = Entry(2, SiloStatus.Dead);
        var suspected = Entry(3, SiloStatus.Dead);
        var joining = Entry(4, SiloStatus.Joining);
        Assert.True(await legacy.InsertAsync(active, "0"));
        Assert.True(await legacy.InsertAsync(dead, "1"));
        Assert.True(await legacy.InsertAsync(suspected, "2"));
        Assert.True(await legacy.InsertAsync(joining, "3"));
        var cutoff = StartTime.AddHours(1);
        suspected.SuspectTimes = [Tuple.Create(active.SiloAddress, cutoff)];
        Assert.True(await legacy.UpdateAsync(suspected, "4"));
        active.IAmAliveTime = StartTime.AddMinutes(1);
        await legacy.HeartbeatAsync(active);

        var beforeUpgrade = await ReadSnapshotAsync(storage, cancellationToken);
        AssertStored(beforeUpgrade, 5, active, dead, suspected, joining);
        AssertReadMatches(beforeUpgrade, await legacy.ReadAllAsync());

        using var services = new ServiceCollection().BuildServiceProvider();
        var clusterOptions = Options.Create(new ClusterOptions { ClusterId = ClusterId });
        var current = new AdoNetClusteringTable(
            services,
            clusterOptions,
            Options.Create(new AdoNetClusteringSiloOptions { Invariant = invariant, ConnectionString = database.CurrentConnectionString }),
            NullLogger<AdoNetClusteringTable>.Instance);
        var gateway = new AdoNetGatewayListProvider(
            NullLogger<AdoNetGatewayListProvider>.Instance,
            services,
            Options.Create(new AdoNetClusteringClientOptions { Invariant = invariant, ConnectionString = database.CurrentConnectionString }),
            Options.Create(new GatewayOptions()),
            clusterOptions);
        var tableError = await Assert.ThrowsAsync<ArgumentException>(() => current.InitializeMembershipTableAsync(true, cancellationToken));
        Assert.Contains("CleanupDefunctSiloEntryKey", tableError.Message, StringComparison.Ordinal);
        var gatewayError = await Assert.ThrowsAsync<ArgumentException>(() => gateway.InitializeGatewayListProvider().WaitAsync(cancellationToken));
        Assert.Contains("CleanupDefunctSiloEntryKey", gatewayError.Message, StringComparison.Ordinal);
        AssertUnchanged(beforeUpgrade, await ReadSnapshotAsync(storage, cancellationToken));

        var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, $"{engine}-Clustering-AtomicWrites.sql"), cancellationToken);
        var duringUpgrade = Entry(5, SiloStatus.Joining);
        if (database is MySqlStorageForTesting mysql)
        {
            var originalRoutine = await ReadMySqlRoutineAsync(storage, cancellationToken);
            var batches = mysql.SplitScript(migration).ToArray();
            Assert.Equal(3, batches.Length);
            Assert.All(batches[0].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                line => Assert.StartsWith("--", line, StringComparison.Ordinal));
            Assert.StartsWith("CREATE PROCEDURE InsertMembershipKeyAtomic(", batches[1].TrimStart(), StringComparison.Ordinal);

            await storage.ExecuteAsync(batches[1], cancellationToken);
            AssertUnchanged(beforeUpgrade, await ReadSnapshotAsync(storage, cancellationToken));
            Assert.Equal(originalRoutine, await ReadMySqlRoutineAsync(storage, cancellationToken));
            Assert.Equal(queries["InsertMembershipKey"], (await ReadQueriesAsync(storage, cancellationToken))["InsertMembershipKey"]);

            // The DELIMITER boundary is a deterministic barrier: the new routine exists, but the
            // catalog is not published yet. A cached caller must still find the original routine.
            Assert.True(await legacy.InsertAsync(duringUpgrade, "5"));
            var beforePublication = await ReadSnapshotAsync(storage, cancellationToken);
            AssertStored(beforePublication, 6, active, dead, suspected, joining, duringUpgrade);
            await storage.ExecuteAsync(batches[2], cancellationToken);
            AssertUnchanged(beforePublication, await ReadSnapshotAsync(storage, cancellationToken));
            Assert.Equal(originalRoutine, await ReadMySqlRoutineAsync(storage, cancellationToken));
            var publishedQueries = await ReadQueriesAsync(storage, cancellationToken);
            Assert.Contains("call InsertMembershipKeyAtomic(", publishedQueries["InsertMembershipKey"], StringComparison.Ordinal);

            // Reapplying must fail at CREATE, not drop/replace a routine which callers can be using.
            var duplicateRoutine = await Assert.ThrowsAsync<MySqlException>(() => storage.ExecuteAsync(batches[1], cancellationToken));
            Assert.Equal(1304, duplicateRoutine.Number); // ER_SP_ALREADY_EXISTS
            AssertUnchanged(beforePublication, await ReadSnapshotAsync(storage, cancellationToken));
            Assert.Equal(originalRoutine, await ReadMySqlRoutineAsync(storage, cancellationToken));
            Assert.Equal(publishedQueries.OrderBy(pair => pair.Key), (await ReadQueriesAsync(storage, cancellationToken)).OrderBy(pair => pair.Key));
        }
        else
        {
            // Both native scripts are one transaction/batch, including PostgreSQL dollar-quoted functions.
            await storage.ExecuteAsync(migration, cancellationToken);
            AssertUnchanged(beforeUpgrade, await ReadSnapshotAsync(storage, cancellationToken));
            if (engine == "SQLServer")
            {
                var updatedQueries = await ReadQueriesAsync(storage, cancellationToken);
                var updated = new CachedLegacyClient(storage, updatedQueries, cancellationToken);
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var oldInsert = InsertAfterBarrierAsync(legacy);
                var newInsert = InsertAfterBarrierAsync(updated);
                start.SetResult();
                Assert.Single(await Task.WhenAll(oldInsert, newInsert), inserted => inserted);

                async Task<bool> InsertAfterBarrierAsync(CachedLegacyClient client)
                {
                    await start.Task.WaitAsync(cancellationToken);
                    return await client.InsertAsync(duringUpgrade, "5");
                }
            }
            else
            {
                Assert.True(await legacy.InsertAsync(duringUpgrade, "5"));
            }
        }

        var upgraded = await ReadSnapshotAsync(storage, cancellationToken);
        AssertStored(upgraded, 6, active, dead, suspected, joining, duringUpgrade);
        await current.InitializeMembershipTableAsync(true, cancellationToken);
        await gateway.InitializeGatewayListProvider().WaitAsync(cancellationToken);
        AssertUnchanged(upgraded, await ReadSnapshotAsync(storage, cancellationToken));
        AssertReadMatches(upgraded, await current.ReadAllAsync(cancellationToken));
        Assert.Equal(
            [SiloAddress.New(active.SiloAddress.Endpoint.Address, active.ProxyPort, active.SiloAddress.Generation).ToGatewayUri()],
            await gateway.GetGateways().WaitAsync(cancellationToken));

        // Alternate new-provider and cached old-query writes, always with the original parameter sets.
        // Cached SQL Server/MySQL inline SQL retains its old behavior: legacy writes here are valid
        // and advance heartbeats, not assertions that the unchanged old SQL has acquired bug fixes.
        var currentEntry = Entry(6, SiloStatus.Active);
        Assert.True(await current.InsertRowAsync(currentEntry, upgraded.NextVersion, cancellationToken));
        duringUpgrade.Status = SiloStatus.Stopping;
        duringUpgrade.IAmAliveTime = StartTime.AddMinutes(2);
        Assert.True(await legacy.UpdateAsync(duringUpgrade, "7"));
        var afterLegacyUpdate = await ReadSnapshotAsync(storage, cancellationToken);
        AssertStored(afterLegacyUpdate, 8, active, dead, suspected, joining, duringUpgrade, currentEntry);

        var capturedMembership = await current.ReadRowAsync(active.SiloAddress, cancellationToken);
        var originalRowToken = Assert.Single(capturedMembership.Members).Item2;
        var originalTableVersion = capturedMembership.Version.Next();
        Assert.Equal(8, capturedMembership.Version.Version);

        active.IAmAliveTime = StartTime.AddMinutes(10);
        await current.UpdateIAmAliveAsync(active, cancellationToken);
        var afterHeartbeat = await ReadSnapshotAsync(storage, cancellationToken);
        AssertStored(afterHeartbeat, 8, active, dead, suspected, joining, duringUpgrade, currentEntry);
        Assert.Equal(afterLegacyUpdate.VersionTimestamp, afterHeartbeat.VersionTimestamp);

        active.Status = SiloStatus.ShuttingDown;
        active.SuspectTimes = [Tuple.Create(joining.SiloAddress, StartTime.AddMinutes(4))];
        active.IAmAliveTime = StartTime.AddMinutes(5);
        Assert.True(await current.UpdateRowAsync(active, originalRowToken, originalTableVersion, cancellationToken));
        var afterUpdate = await ReadSnapshotAsync(storage, cancellationToken);
        AssertStored(afterUpdate, 9, active, dead, suspected, joining, duringUpgrade, currentEntry);

        active.IAmAliveTime = StartTime.AddMinutes(3);
        await current.UpdateIAmAliveAsync(active, cancellationToken);
        var afterBlindHeartbeat = await ReadSnapshotAsync(storage, cancellationToken);
        AssertStored(afterBlindHeartbeat, 9, active, dead, suspected, joining, duringUpgrade, currentEntry);
        Assert.Equal(afterUpdate.VersionTimestamp, afterBlindHeartbeat.VersionTimestamp);
        active.IAmAliveTime = StartTime.AddMinutes(20);
        await current.UpdateIAmAliveAsync(active, cancellationToken);
        var afterNewHeartbeat = await ReadSnapshotAsync(storage, cancellationToken);
        AssertStored(afterNewHeartbeat, 9, active, dead, suspected, joining, duringUpgrade, currentEntry);
        Assert.Equal(afterUpdate.VersionTimestamp, afterNewHeartbeat.VersionTimestamp);

        await current.UpdateIAmAliveAsync(Entry(10, SiloStatus.Active), cancellationToken);
        AssertUnchanged(afterNewHeartbeat, await ReadSnapshotAsync(storage, cancellationToken));

        duringUpgrade.IAmAliveTime = StartTime.AddMinutes(4);
        await legacy.HeartbeatAsync(duringUpgrade);
        var legacyEntry = Entry(7, SiloStatus.Joining);
        Assert.True(await legacy.InsertAsync(legacyEntry, "9"));
        var stable = await ReadSnapshotAsync(storage, cancellationToken);
        AssertStored(stable, 10, active, dead, suspected, joining, duringUpgrade, currentEntry, legacyEntry);
        AssertReadMatches(stable, await current.ReadAllAsync(cancellationToken));
        AssertReadMatches(stable, await legacy.ReadAllAsync());

        var absent = Entry(8, SiloStatus.Dead);
        await AssertRejectedAsync(() => current.InsertRowAsync(absent, afterUpdate.NextVersion, cancellationToken));
        var changed = Entry(1, SiloStatus.Dead);
        changed.IAmAliveTime = StartTime.AddDays(1);
        changed.SuspectTimes = [Tuple.Create(joining.SiloAddress, StartTime.AddDays(1))];
        await AssertRejectedAsync(() => current.UpdateRowAsync(changed, afterUpdate.Etag, afterUpdate.NextVersion, cancellationToken));
        await AssertRejectedAsync(() => current.InsertRowAsync(changed, stable.NextVersion, cancellationToken));
        await AssertRejectedAsync(() => current.UpdateRowAsync(changed, afterUpdate.Etag, stable.NextVersion, cancellationToken));
        await AssertRejectedAsync(() => current.UpdateRowAsync(absent, stable.Etag, stable.NextVersion, cancellationToken));

        await current.CleanupDefunctSiloEntriesAsync(new DateTimeOffset(cutoff), cancellationToken);
        var afterCleanup = await ReadSnapshotAsync(storage, cancellationToken);
        AssertStored(afterCleanup, 10, active, suspected, joining, duringUpgrade, currentEntry, legacyEntry);
        Assert.Equal(stable.VersionTimestamp, afterCleanup.VersionTimestamp);
        Assert.Equal(stable.Members.Where(member => !member.SiloAddress.Equals(dead.SiloAddress)), afterCleanup.Members);
        AssertReadMatches(afterCleanup, await current.ReadAllAsync(cancellationToken));

        // A binary rollback/restart reloads the upgraded catalog rather than retaining cached SQL.
        // Exercise the same old client and parameter sets against that distinct compatibility path.
        var reloadedQueries = await ReadQueriesAsync(storage, cancellationToken);
        Assert.All(queries.Keys, key => Assert.Contains(key, reloadedQueries.Keys));
        var reloadedLegacy = new CachedLegacyClient(storage, reloadedQueries, cancellationToken);
        Assert.False(await reloadedLegacy.InsertVersionAsync());
        AssertUnchanged(afterCleanup, await ReadSnapshotAsync(storage, cancellationToken));
        var restartedEntry = Entry(9, SiloStatus.Joining);
        Assert.True(await reloadedLegacy.InsertAsync(restartedEntry, afterCleanup.Etag));
        var afterRestartedInsert = await ReadSnapshotAsync(storage, cancellationToken);
        AssertStored(afterRestartedInsert, 11, active, suspected, joining, duringUpgrade, currentEntry, legacyEntry, restartedEntry);
        restartedEntry.Status = SiloStatus.Stopping;
        restartedEntry.SuspectTimes = [Tuple.Create(joining.SiloAddress, StartTime.AddMinutes(3))];
        restartedEntry.IAmAliveTime = StartTime.AddMinutes(5);
        Assert.True(await reloadedLegacy.UpdateAsync(restartedEntry, afterRestartedInsert.Etag));
        var afterRestartedUpdate = await ReadSnapshotAsync(storage, cancellationToken);
        AssertStored(afterRestartedUpdate, 12, active, suspected, joining, duringUpgrade, currentEntry, legacyEntry, restartedEntry);
        restartedEntry.IAmAliveTime = StartTime.AddMinutes(6);
        await reloadedLegacy.HeartbeatAsync(restartedEntry);
        var afterRestart = await ReadSnapshotAsync(storage, cancellationToken);
        AssertStored(afterRestart, 12, active, suspected, joining, duringUpgrade, currentEntry, legacyEntry, restartedEntry);
        Assert.Equal(afterRestartedUpdate.VersionTimestamp, afterRestart.VersionTimestamp);
        AssertReadMatches(afterRestart, await reloadedLegacy.ReadAllAsync());
        AssertReadMatches(afterRestart, await legacy.ReadAllAsync());
        AssertReadMatches(afterRestart, await current.ReadAllAsync(cancellationToken));

        if (engine is "SQLServer" or "MySQL")
        {
            // Frozen inline SQL keeps its original semantics until the caller reloads the catalog.
            Assert.False(await legacy.UpdateAsync(absent, afterRestart.Etag));
            var afterCachedFailure = await ReadSnapshotAsync(storage, cancellationToken);
            Assert.Equal(afterRestart.Version + 1, afterCachedFailure.Version);
            Assert.Equal(afterRestart.Members, afterCachedFailure.Members);
            Assert.False(await reloadedLegacy.UpdateAsync(absent, afterCachedFailure.Etag));
            AssertUnchanged(afterCachedFailure, await ReadSnapshotAsync(storage, cancellationToken));

            await reloadedLegacy.CleanupAsync(cutoff);
            AssertUnchanged(afterCachedFailure, await ReadSnapshotAsync(storage, cancellationToken));
            await legacy.CleanupAsync(cutoff);
            var afterCachedCleanup = await ReadSnapshotAsync(storage, cancellationToken);
            Assert.Equal(afterCachedFailure.Version, afterCachedCleanup.Version);
            Assert.Equal(afterCachedFailure.VersionTimestamp, afterCachedCleanup.VersionTimestamp);
            Assert.Equal(
                afterCachedFailure.Members.Where(member => member.Status == SiloStatus.Active || member.IAmAliveTime >= cutoff),
                afterCachedCleanup.Members);
        }

        if (engine == "MySQL")
        {
            var beforeOverlap = await ReadSnapshotAsync(storage, cancellationToken);
            var overlappingEntry = Entry(11, SiloStatus.Joining);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldInsert = InsertAfterBarrierAsync(legacy);
            var newInsert = InsertAfterBarrierAsync(reloadedLegacy);
            start.SetResult();
            var outcomes = await Task.WhenAll(oldInsert, newInsert);
            Assert.Single(outcomes, outcome => outcome.Inserted);
            foreach (var outcome in outcomes)
            {
                if (outcome.Error is not null)
                {
                    Assert.Equal(1213, Assert.IsType<MySqlException>(outcome.Error).Number);
                }
            }

            var afterOverlap = await ReadSnapshotAsync(storage, cancellationToken);
            Assert.Equal(beforeOverlap.Version + 1, afterOverlap.Version);
            Assert.Equal(beforeOverlap.Members.Append(StoredMember.FromEntry(overlappingEntry)), afterOverlap.Members);
            Assert.False(await reloadedLegacy.InsertAsync(overlappingEntry, beforeOverlap.Etag));
            AssertUnchanged(afterOverlap, await ReadSnapshotAsync(storage, cancellationToken));

            async Task<(bool Inserted, Exception? Error)> InsertAfterBarrierAsync(CachedLegacyClient client)
            {
                await start.Task.WaitAsync(cancellationToken);
                var inserted = false;
                var error = await Record.ExceptionAsync(async () => inserted = await client.InsertAsync(overlappingEntry, beforeOverlap.Etag));
                return (inserted, error);
            }
        }

        if (engine == "SQLServer")
        {
            var beforeOverlap = await ReadSnapshotAsync(storage, cancellationToken);
            var overlappingEntry = Entry(12, SiloStatus.Joining);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var insertion = InsertAfterBarrierAsync();
            var reading = ReadAfterBarrierAsync();
            start.SetResult();
            await Task.WhenAll(insertion, reading);
            var (inserted, insertError) = await insertion;
            var (observed, readError) = await reading;
            if (insertError is not null)
            {
                Assert.Equal(1205, Assert.IsType<SqlException>(insertError).Number);
                AssertUnchanged(beforeOverlap, await ReadSnapshotAsync(storage, cancellationToken));
                var refreshed = await legacy.ReadAllAsync();
                Assert.True(await legacy.InsertAsync(overlappingEntry, refreshed.Version.VersionEtag));
            }
            else
            {
                Assert.True(inserted);
            }

            var afterOverlap = await ReadSnapshotAsync(storage, cancellationToken);
            Assert.Equal(beforeOverlap.Version + 1, afterOverlap.Version);
            Assert.Equal(beforeOverlap.Members.Append(StoredMember.FromEntry(overlappingEntry)), afterOverlap.Members);
            if (readError is not null)
            {
                Assert.Equal(1205, Assert.IsType<SqlException>(readError).Number);
                observed = await current.ReadRowAsync(overlappingEntry.SiloAddress, cancellationToken);
            }

            Assert.NotNull(observed);
            if (observed.Version.Version == beforeOverlap.Version)
            {
                Assert.Empty(observed.Members);
            }
            else
            {
                Assert.Equal(afterOverlap.Version, observed.Version.Version);
                Assert.Equal(StoredMember.FromEntry(overlappingEntry), StoredMember.FromEntry(Assert.Single(observed.Members).Item1));
            }

            async Task<(bool Inserted, Exception? Error)> InsertAfterBarrierAsync()
            {
                await start.Task.WaitAsync(cancellationToken);
                var inserted = false;
                var error = await Record.ExceptionAsync(async () => inserted = await legacy.InsertAsync(overlappingEntry, beforeOverlap.Etag));
                return (inserted, error);
            }

            async Task<(MembershipTableData? Snapshot, Exception? Error)> ReadAfterBarrierAsync()
            {
                await start.Task.WaitAsync(cancellationToken);
                MembershipTableData? snapshot = null;
                var error = await Record.ExceptionAsync(async () => snapshot = await current.ReadRowAsync(overlappingEntry.SiloAddress, cancellationToken));
                return (snapshot, error);
            }
        }

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

    private static async Task<string[]> ReadMySqlRoutineAsync(IRelationalStorage storage, CancellationToken cancellationToken) =>
        Assert.Single(await storage.ReadAsync(
            "SHOW CREATE PROCEDURE InsertMembershipKey",
            record => Enumerable.Range(0, record.FieldCount).Select(index => record.GetString(index)).ToArray(),
            null,
            cancellationToken));

    private static async Task<Snapshot> ReadSnapshotAsync(IRelationalStorage storage, CancellationToken cancellationToken)
    {
        var version = Assert.Single(await storage.ReadAsync(
            "SELECT Version, Timestamp FROM OrleansMembershipVersionTable WHERE DeploymentId = @DeploymentId",
            record => (Version: record.GetInt32(0), Timestamp: record.GetDateTime(1)),
            command => _ = new DbStoredQueries.Columns(command) { DeploymentId = ClusterId },
            cancellationToken));
        var members = await storage.ReadAsync(
            """
            SELECT DeploymentId, Address, Port, Generation, SiloName, HostName, Status, ProxyPort, SuspectTimes, StartTime, IAmAliveTime
            FROM OrleansMembershipTable WHERE DeploymentId = @DeploymentId ORDER BY Address, Port, Generation
            """,
            record => new StoredMember(
                record.GetString(0),
                SiloAddress.New(IPAddress.Parse(record.GetString(1)), record.GetInt32(2), record.GetInt32(3)),
                record.GetString(4),
                record.GetString(5),
                (SiloStatus)record.GetInt32(6),
                record.GetInt32(7),
                record.IsDBNull(8) ? null : record.GetString(8),
                record.GetDateTime(9),
                record.GetDateTime(10)),
            command => _ = new DbStoredQueries.Columns(command) { DeploymentId = ClusterId },
            cancellationToken);
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
        string DeploymentId,
        SiloAddress SiloAddress,
        string SiloName,
        string HostName,
        SiloStatus Status,
        int ProxyPort,
        string? SuspectTimes,
        DateTime StartTime,
        DateTime IAmAliveTime)
    {
        public static StoredMember FromEntry(MembershipEntry entry) => new(
            ClusterId,
            entry.SiloAddress,
            entry.SiloName,
            entry.HostName,
            entry.Status,
            entry.ProxyPort,
            entry.SuspectTimes is null ? null : string.Join("|", entry.SuspectTimes.Select(vote => $"{vote.Item1.ToParsableString()},{LogFormatter.PrintDate(vote.Item2)}")),
            entry.StartTime,
            entry.IAmAliveTime);
    }

    // This intentionally never constructs current RelationalOrleansQueries (which rejects the old
    // catalog) or reloads QueryText after migration. Only the server-side routines can change.
    private sealed class CachedLegacyClient(IRelationalStorage storage, Dictionary<string, string> queries, CancellationToken cancellationToken)
    {
        public Task<bool> InsertVersionAsync() =>
            WriteAsync("InsertMembershipVersionKey", command => _ = new DbStoredQueries.Columns(command) { DeploymentId = ClusterId });

        public Task<bool> InsertAsync(MembershipEntry entry, string etag) =>
            WriteAsync("InsertMembershipKey", command => _ = new DbStoredQueries.Columns(command)
            {
                DeploymentId = ClusterId,
                SiloAddress = entry.SiloAddress,
                SiloName = entry.SiloName,
                HostName = entry.HostName,
                Status = entry.Status,
                ProxyPort = entry.ProxyPort,
                StartTime = entry.StartTime,
                IAmAliveTime = entry.IAmAliveTime,
                Version = etag
            });

        public Task<bool> UpdateAsync(MembershipEntry entry, string etag) =>
            WriteAsync("UpdateMembershipKey", command => _ = new DbStoredQueries.Columns(command)
            {
                DeploymentId = ClusterId,
                SiloAddress = entry.SiloAddress,
                Status = entry.Status,
                SuspectTimes = entry.SuspectTimes,
                IAmAliveTime = entry.IAmAliveTime,
                Version = etag
            });

        public Task HeartbeatAsync(MembershipEntry entry) =>
            storage.ExecuteAsync(queries["UpdateIAmAlivetimeKey"], command => _ = new DbStoredQueries.Columns(command)
            {
                DeploymentId = ClusterId,
                SiloAddress = entry.SiloAddress,
                IAmAliveTime = entry.IAmAliveTime
            }, cancellationToken: cancellationToken);

        public Task CleanupAsync(DateTime cutoff) =>
            storage.ExecuteAsync(queries["CleanupDefunctSiloEntriesKey"], command => _ = new DbStoredQueries.Columns(command)
            {
                DeploymentId = ClusterId,
                IAmAliveTime = cutoff
            }, cancellationToken: cancellationToken);

        public async Task<MembershipTableData> ReadAllAsync()
        {
            var rows = (await storage.ReadAsync(
                queries["MembershipReadAllKey"],
                DbStoredQueries.Converters.GetMembershipEntry,
                command => _ = new DbStoredQueries.Columns(command) { DeploymentId = ClusterId },
                cancellationToken)).ToArray();
            var version = rows[0].Item2;
            var etag = version.ToString(CultureInfo.InvariantCulture);
            Assert.All(rows, row => Assert.Equal(version, row.Item2));
            return new MembershipTableData(
                rows.Where(row => row.Item1 is not null).Select(row => Tuple.Create(row.Item1!, etag)).ToList(),
                new TableVersion(version, etag));
        }

        private async Task<bool> WriteAsync(string key, Action<IDbCommand> parameters) =>
            Assert.Single(await storage.ReadAsync(queries[key], DbStoredQueries.Converters.GetSingleBooleanValue, parameters, cancellationToken));
    }
}
