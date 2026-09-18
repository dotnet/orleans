using System.Net;
using Cassandra;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Clustering.Cassandra;
using Orleans.Clustering.Cassandra.Hosting;
using Orleans.Configuration;
using Orleans.Messaging;
using Tester.Cassandra.Utility;
using Xunit;

namespace Tester.Cassandra.Clustering;

/// <summary>
/// Tests for Orleans membership table operations using Apache Cassandra as the backing store.
/// </summary>
[TestCategory("Cassandra"), TestCategory("Clustering"), TestCategory("Functional")]
[Collection("Cassandra")]
[TestSuite("Functional")]
[TestProvider("Cassandra")]
[TestArea("Membership")]
public sealed class CassandraClusteringTableTests : IClassFixture<CassandraContainer>
{
    private readonly CassandraContainer _cassandraContainer;
    private readonly ITestOutputHelper _testOutputHelper;
    private static readonly string HostName = Dns.GetHostName();
    private static int _generation;

    public CassandraClusteringTableTests(CassandraContainer cassandraContainer, ITestOutputHelper testOutputHelper)
    {
        cassandraContainer.EnsurePreconditionsMet();
        _cassandraContainer = cassandraContainer;
        _cassandraContainer.Name = nameof(Cassandra);
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public async Task MembershipTable_GetGateways()
    {
        var (membershipTable, gatewayListProvider) = await CreateNewMembershipTableAsync(
            TestContext.Current.CancellationToken);

        var membershipEntries = Enumerable.Range(0, 10).Select(_ => CreateMembershipEntryForTest()).ToArray();

        membershipEntries[3].Status = SiloStatus.Active;
        membershipEntries[3].ProxyPort = 0;
        membershipEntries[5].Status = SiloStatus.Active;
        membershipEntries[9].Status = SiloStatus.Active;

        var data = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(data);
        Assert.Empty(data.Members);

        var version = data.Version;
        foreach (var membershipEntry in membershipEntries)
        {
            Assert.True(await membershipTable.InsertRowAsync(membershipEntry, version.Next(), TestContext.Current.CancellationToken));
            version = (await membershipTable.ReadRowAsync(membershipEntry.SiloAddress, TestContext.Current.CancellationToken)).Version;
        }

        var gateways = await gatewayListProvider.GetGateways();

        var entries = new List<string>(gateways.Select(g => g.ToString()));

        // only members with a non-zero Gateway port
        Assert.DoesNotContain(membershipEntries[3].SiloAddress.ToGatewayUri().ToString(), entries);

        // only Active members
        Assert.Contains(membershipEntries[5].SiloAddress.ToGatewayUri().ToString(), entries);
        Assert.Contains(membershipEntries[9].SiloAddress.ToGatewayUri().ToString(), entries);
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task MembershipTable_ReadAll_EmptyTable()
    {
        var (membershipTable, _) = await CreateNewMembershipTableAsync(
            TestContext.Current.CancellationToken);

        var data = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(data);

        _testOutputHelper.WriteLine("Membership.ReadAll returned TableVersion={0} Data={1}", data.Version, data);

        Assert.Empty(data.Members);
        Assert.NotNull(data.Version.VersionEtag);
        Assert.Equal(0, data.Version.Version);
    }

    [Fact]
    public async Task MembershipTable_InsertRow()
    {
        var (membershipTable, _) = await CreateNewMembershipTableAsync(
            TestContext.Current.CancellationToken);

        var membershipEntry = CreateMembershipEntryForTest();

        var data = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(data);
        Assert.Empty(data.Members);

        var nextTableVersion = data.Version.Next();

        var ok = await membershipTable.InsertRowAsync(membershipEntry, nextTableVersion, TestContext.Current.CancellationToken);
        Assert.True(ok, "InsertRow failed");

        data = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, data.Version.Version);

        Assert.Single(data.Members);
    }

    [Fact]
    public async Task MembershipTable_ReadRow_Insert_Read()
    {
        var (membershipTable, gatewayProvider) = await CreateNewMembershipTableAsync(
            "Phalanx",
            "blu",
            TestContext.Current.CancellationToken);

        MembershipTableData data = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);

        _testOutputHelper.WriteLine("Membership.ReadAll returned TableVersion={0} Data={1}", data.Version, data);

        Assert.Empty(data.Members);

        TableVersion newTableVersion = data.Version.Next();

        MembershipEntry newEntry = CreateMembershipEntryForTest();
        bool ok = await membershipTable.InsertRowAsync(newEntry, newTableVersion, TestContext.Current.CancellationToken);
        Assert.True(ok, "InsertRow failed");

        ok = await membershipTable.InsertRowAsync(newEntry, newTableVersion, TestContext.Current.CancellationToken);
        Assert.False(ok, "InsertRow should have failed - same entry, old table version");

        ok = await membershipTable.InsertRowAsync(CreateMembershipEntryForTest(), newTableVersion, TestContext.Current.CancellationToken);
        Assert.False(ok, "InsertRow should have failed - new entry, old table version");

        data = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, data.Version.Version);

        TableVersion nextTableVersion = data.Version.Next();

        ok = await membershipTable.InsertRowAsync(newEntry, nextTableVersion, TestContext.Current.CancellationToken);
        Assert.False(ok, "InsertRow should have failed - duplicate entry");

        data = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);
        Assert.Single(data.Members);

        data = await membershipTable.ReadRowAsync(newEntry.SiloAddress, TestContext.Current.CancellationToken);
        Assert.Equal(newTableVersion.Version, data.Version.Version);

        _testOutputHelper.WriteLine("Membership.ReadAll returned TableVersion={0} Data={1}", data.Version, data);

        Assert.Single(data.Members);
        Assert.NotNull(data.Version.VersionEtag);

        Assert.NotEqual(newTableVersion.VersionEtag, data.Version.VersionEtag);
        Assert.Equal(newTableVersion.Version, data.Version.Version);

        var membershipEntry = data.Members[0].Item1;
        string eTag = data.Members[0].Item2;
        _testOutputHelper.WriteLine("Membership.ReadRow returned MembershipEntry ETag={0} Entry={1}", eTag, membershipEntry);

        Assert.NotNull(eTag);
        Assert.NotNull(membershipEntry);
    }

    [Fact]
    public async Task MembershipTable_ReadAll_Insert_ReadAll()
    {
        var (membershipTable, _) = await CreateNewMembershipTableAsync(
            TestContext.Current.CancellationToken);

        var data = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);
        _testOutputHelper.WriteLine("Membership.ReadAll returned TableVersion={0} Data={1}", data.Version, data);

        Assert.Empty(data.Members);

        var newTableVersion = data.Version.Next();

        var newEntry = CreateMembershipEntryForTest();
        var ok = await membershipTable.InsertRowAsync(newEntry, newTableVersion, TestContext.Current.CancellationToken);
        Assert.True(ok, "InsertRow failed");

        data = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);
        _testOutputHelper.WriteLine("Membership.ReadAll returned TableVersion={0} Data={1}", data.Version, data);

        Assert.Single(data.Members);
        Assert.NotNull(data.Version.VersionEtag);

        Assert.NotEqual(newTableVersion.VersionEtag, data.Version.VersionEtag);
        Assert.Equal(newTableVersion.Version, data.Version.Version);

        var membershipEntry = data.Members[0].Item1;
        var eTag = data.Members[0].Item2;
        _testOutputHelper.WriteLine("Membership.ReadAll returned MembershipEntry ETag={0} Entry={1}", eTag, membershipEntry);

        Assert.NotNull(eTag);
        Assert.NotNull(membershipEntry);
    }

    [Fact]
    public async Task MembershipTable_UpdateRow()
    {
        var (membershipTable, _) = await CreateNewMembershipTableAsync(
            TestContext.Current.CancellationToken);

        var tableData = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(tableData.Version);

        Assert.Equal(0, tableData.Version.Version);
        Assert.Empty(tableData.Members);

        for (var i = 1; i < 10; i++)
        {
            var siloEntry = CreateMembershipEntryForTest();

            siloEntry.SuspectTimes =
            [
                new Tuple<SiloAddress, DateTime>(CreateSiloAddressForTest(), GetUtcNowWithSecondsResolution().AddSeconds(1)),
                new Tuple<SiloAddress, DateTime>(CreateSiloAddressForTest(), GetUtcNowWithSecondsResolution().AddSeconds(2))
            ];

            var tableVersion = Assert.IsType<TableVersion>(tableData.Version.Next());

            _testOutputHelper.WriteLine("Calling InsertRow with Entry = {0} TableVersion = {1}", siloEntry, tableVersion);
            var ok = await membershipTable.InsertRowAsync(siloEntry, tableVersion, TestContext.Current.CancellationToken);
            Assert.True(ok, "InsertRow failed");

            tableData = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);

            var etagBefore = tableData.TryGet(siloEntry.SiloAddress)?.Item2;

            Assert.NotNull(etagBefore);

            _testOutputHelper.WriteLine(
                "Calling UpdateRow with Entry = {0} correct eTag = {1} old version={2}",
                siloEntry,
                etagBefore,
                tableVersion.ToString());
            ok = await membershipTable.UpdateRowAsync(siloEntry, etagBefore, tableVersion, TestContext.Current.CancellationToken);
            Assert.False(ok, $"row update should have failed - Table Data = {tableData}");
            tableData = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);

            tableVersion = Assert.IsType<TableVersion>(tableData.Version.Next());

            _testOutputHelper.WriteLine(
                "Calling UpdateRow with Entry = {0} correct eTag = {1} correct version={2}",
                siloEntry,
                etagBefore,
                tableVersion.ToString());

            ok = await membershipTable.UpdateRowAsync(siloEntry, etagBefore, tableVersion, TestContext.Current.CancellationToken);

            Assert.True(ok, $"UpdateRow failed - Table Data = {tableData}");

            _testOutputHelper.WriteLine(
                "Calling UpdateRow with Entry = {0} old eTag = {1} old version={2}",
                siloEntry,
                etagBefore,
                tableVersion.ToString());
            ok = await membershipTable.UpdateRowAsync(siloEntry, etagBefore, tableVersion, TestContext.Current.CancellationToken);
            Assert.False(ok, $"row update should have failed - Table Data = {tableData}");

            tableData = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);

            var tuple = tableData.TryGet(siloEntry.SiloAddress);
            Assert.NotNull(tuple);

            Assert.Equal(tuple.Item1.ToFullString(), siloEntry.ToFullString());

            var etagAfter = tuple.Item2;

            _testOutputHelper.WriteLine(
                "Calling UpdateRow with Entry = {0} correct eTag = {1} old version={2}",
                siloEntry,
                etagAfter,
                tableVersion.ToString());

            ok = await membershipTable.UpdateRowAsync(siloEntry, etagAfter, tableVersion, TestContext.Current.CancellationToken);

            Assert.False(ok, $"row update should have failed - Table Data = {tableData}");

            tableData = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);

            etagBefore = etagAfter;

            etagAfter = tableData.TryGet(siloEntry.SiloAddress)?.Item2;

            Assert.Equal(etagBefore, etagAfter);
            Assert.NotNull(tableData.Version);
            Assert.Equal(tableVersion!.Version, tableData.Version.Version);

            Assert.Equal(i, tableData.Members.Count);
        }
    }

    [Fact]
    public async Task MembershipTable_ManyMembershipTables()
    {
        var tasks = new List<Task>();
        for (var i = 0; i < 50; i++)
        {
            tasks.Add(Task.Run(
                async () =>
                {
                    await Task.Yield();
                    var (membershipTable, _) = await CreateNewMembershipTableAsync(
                        TestContext.Current.CancellationToken);
                },
                TestContext.Current.CancellationToken));
        }
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task MembershipTable_UpdateRowInParallel()
    {
        var (membershipTable, _) = await CreateNewMembershipTableAsync(
            TestContext.Current.CancellationToken);

        var tableData = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);

        var data = CreateMembershipEntryForTest();

        var newTableVer = tableData.Version.Next();

        var insertions = Task.WhenAll(Enumerable.Range(1, 20).Select(_ =>
            membershipTable.InsertRowAsync(data, newTableVer, TestContext.Current.CancellationToken)));

        Assert.True((await insertions).Single(x => x), "InsertRow failed");

        await Task.WhenAll(Enumerable.Range(1, 19).Select(async _ =>
        {
            var done = false;
            do
            {
                var updatedTableData = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);
                var updatedRow = updatedTableData.TryGet(data.SiloAddress);

                await Task.Delay(10, TestContext.Current.CancellationToken);
                if (updatedRow is null) continue;

                var tableVersion = updatedTableData.Version.Next();
                done = await membershipTable.UpdateRowAsync(updatedRow.Item1, updatedRow.Item2, tableVersion, TestContext.Current.CancellationToken);
            } while (!done);
        })).WithTimeout(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);


        tableData = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(tableData.Version);

        Assert.Equal(20, tableData.Version.Version);

        Assert.Single(tableData.Members);
    }

    [Theory]
    [InlineData(true, SiloStatus.Active)]
    [InlineData(false, SiloStatus.Active)]
    [InlineData(true, SiloStatus.Dead)]
    [InlineData(false, SiloStatus.Dead)]
    public async Task MembershipTable_UpdateIAmAlive(bool cassandraTtl, SiloStatus status)
    {
        var testCancellationToken = TestContext.Current.CancellationToken;

        ISession ttlSession = await CreateSession(testCancellationToken);

        var serviceId = $"Service_{Guid.NewGuid():N}";
        var clusterId = $"Cluster_{Guid.NewGuid():N}";
        var clusterIdentifier = $"{serviceId}-{clusterId}";
        var (membershipTable, _) = await CreateNewMembershipTableAsync(
            serviceId,
            clusterId,
            testCancellationToken,
            cassandraTtl: cassandraTtl);

        var tableData = await membershipTable.ReadAllAsync(testCancellationToken);

        var newTableVersion = tableData.Version.Next();
        var newEntry = CreateMembershipEntryForTest();
        newEntry.Status = status;
        newEntry.SuspectTimes = [Tuple.Create(CreateSiloAddressForTest(), newEntry.StartTime)];
        var ok = await membershipTable.InsertRowAsync(newEntry, newTableVersion, testCancellationToken);
        Assert.True(ok);
        MembershipEntry originalMembershipEntry = (await membershipTable.ReadAllAsync(testCancellationToken))
            .Members.First(e => e.Item1.SiloAddress.Equals(newEntry.SiloAddress))
            .Item1;
        Assert.Equal(newEntry.SuspectTimes, originalMembershipEntry.SuspectTimes);

        await ValidateTtlValues();

        var amAliveTime = GetUtcNowWithSecondsResolution().AddSeconds(5);

        // This mimics the arguments MembershipOracle.OnIAmAliveUpdateInTableTimer passes in
        var entry = new MembershipEntry
        {
            SiloAddress = newEntry.SiloAddress,
            IAmAliveTime = amAliveTime
        };

        await membershipTable.UpdateIAmAliveAsync(entry, testCancellationToken);

        tableData = await membershipTable.ReadAllAsync(testCancellationToken);
        MembershipEntry updatedMember = tableData.Members
            .First(e => e.Item1.SiloAddress.Equals(newEntry.SiloAddress))
            .Item1;

        Assert.Equal(amAliveTime, updatedMember.IAmAliveTime);
        Assert.Equal(newTableVersion.Version, tableData.Version.Version);

        // Validate the rest of the data is still the same after the update
        Assert.Equal(originalMembershipEntry.SiloAddress, updatedMember.SiloAddress);
        Assert.Equal(originalMembershipEntry.SiloName, updatedMember.SiloName);
        Assert.Equal(originalMembershipEntry.HostName, updatedMember.HostName);
        Assert.Equal(originalMembershipEntry.Status, updatedMember.Status);
        Assert.Equal(originalMembershipEntry.ProxyPort, updatedMember.ProxyPort);
        Assert.Equal(originalMembershipEntry.SuspectTimes, updatedMember.SuspectTimes);
        Assert.Equal(originalMembershipEntry.StartTime, updatedMember.StartTime);

        await ValidateTtlValues();
        foreach (var heartbeat in new[] { amAliveTime.AddSeconds(-1), amAliveTime })
        {
            await membershipTable.UpdateIAmAliveAsync(new MembershipEntry
            {
                SiloAddress = newEntry.SiloAddress,
                IAmAliveTime = heartbeat
            }, testCancellationToken);
            var unchanged = await membershipTable.ReadRowAsync(newEntry.SiloAddress, testCancellationToken);
            Assert.Equal(updatedMember.ToFullString(), Assert.Single(unchanged.Members).Item1.ToFullString());
            Assert.Equal(tableData.Version, unchanged.Version);
        }

        var beforeCleanup = await membershipTable.ReadAllAsync(testCancellationToken);
        await membershipTable.CleanupDefunctSiloEntriesAsync(new DateTimeOffset(amAliveTime.AddSeconds(1)), testCancellationToken);
        var afterCleanup = await membershipTable.ReadAllAsync(testCancellationToken);
        if (status == SiloStatus.Dead)
        {
            Assert.Empty(afterCleanup.Members);
            await membershipTable.UpdateIAmAliveAsync(entry, testCancellationToken);
            Assert.Empty((await membershipTable.ReadAllAsync(testCancellationToken)).Members);
        }
        else
        {
            Assert.Equal(updatedMember.ToFullString(), Assert.Single(afterCleanup.Members).Item1.ToFullString());
        }

        Assert.Equal(beforeCleanup.Version, afterCleanup.Version);
        await membershipTable.CleanupDefunctSiloEntriesAsync(new DateTimeOffset(amAliveTime.AddSeconds(1)), testCancellationToken);
        Assert.Equal(afterCleanup.Version, (await membershipTable.ReadAllAsync(testCancellationToken)).Version);

        return;

        async Task ValidateTtlValues()
        {
            // Cassandra columns that are part of the primary key are not available with the TTL command
            // See https://issues.apache.org/jira/browse/CASSANDRA-9312
            Row ttlResult = (await ttlSession.ExecuteAsync(new SimpleStatement(
                    """
                    SELECT
                        TTL (version) as version_tll,
                        TTL (silo_name) as siloname_ttl,
                        TTL (host_name) as hostname_ttl,
                        TTL (status) as status_ttl,
                        TTL (proxy_port) as proxyport_ttl,
                        TTL (suspect_times) as suspecttimes_ttl,
                        TTL (start_time) as starttime_ttl,
                        TTL (i_am_alive_time) as iamalivetime_ttl
                    FROM membership
                    WHERE partition_key = ?
                      AND address = ?
                      AND port = ?
                      AND generation = ?
                    """,
                    clusterIdentifier,
                    newEntry.SiloAddress.Endpoint.Address.ToString(),
                    newEntry.SiloAddress.Endpoint.Port,
                    newEntry.SiloAddress.Generation)))
                .First();

            object versionTtl = ttlResult["version_tll"];
            object siloNameTtl = ttlResult["siloname_ttl"];
            object hostNameTtl = ttlResult["hostname_ttl"];
            object statusTtl = ttlResult["status_ttl"];
            object proxyPortTtl = ttlResult["proxyport_ttl"];
            object suspectTimesTtl = ttlResult["suspecttimes_ttl"];
            object startTimeTtl = ttlResult["starttime_ttl"];
            object iAmAliveTtl = ttlResult["iamalivetime_ttl"];

            Assert.Null(versionTtl);
            var rowTtls = new[] { siloNameTtl, hostNameTtl, statusTtl, proxyPortTtl, suspectTimesTtl, startTimeTtl, iAmAliveTtl };
            if (cassandraTtl && status == SiloStatus.Dead)
            {
                var ttl = Assert.IsType<int>(statusTtl);
                Assert.InRange(ttl, 1, 20);
                Assert.All(rowTtls, value => Assert.Equal(ttl, Assert.IsType<int>(value)));
            }
            else
            {
                Assert.All(rowTtls, Assert.Null);
            }
        }
    }

    [Fact]
    public async Task MembershipTable_Ttl_ExpiresDeadRowsAndPreservesLiveRowsAndVersion()
    {
        var token = TestContext.Current.CancellationToken;
        var (table, _) = await CreateNewMembershipTableAsync(token, cassandraTtl: true);
        var live = CreateMembershipEntryForTest();
        live.Status = SiloStatus.Active;
        var dead = CreateMembershipEntryForTest();
        dead.Status = SiloStatus.Dead;
        var data = await table.ReadAllAsync(token);
        Assert.True(await table.InsertRowAsync(live, data.Version.Next(), token));
        data = await table.ReadAllAsync(token);
        Assert.True(await table.InsertRowAsync(dead, data.Version.Next(), token));
        var before = await table.ReadAllAsync(token);
        Assert.Equal(2, before.Members.Count);

        var deadline = DateTime.UtcNow.AddSeconds(45);
        do
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            data = await table.ReadAllAsync(token);
        }
        while (data.TryGet(dead.SiloAddress) is not null && DateTime.UtcNow < deadline);

        Assert.Equal(live.ToFullString(), Assert.Single(data.Members).Item1.ToFullString());
        Assert.Equal(before.Version, data.Version);
        await table.UpdateIAmAliveAsync(new MembershipEntry
        {
            SiloAddress = dead.SiloAddress,
            IAmAliveTime = DateTime.UtcNow
        }, token);
        var retired = await table.ReadRowAsync(dead.SiloAddress, token);
        Assert.Empty(retired.Members);
        Assert.Equal(before.Version, retired.Version);
    }

    [Fact]
    public async Task MembershipTable_NonTtlHeartbeat_AbsentRow_PreservesExistingRowsAndVersion()
    {
        var token = TestContext.Current.CancellationToken;
        var (table, _) = await CreateNewMembershipTableAsync(token);
        var entry = CreateMembershipEntryForTest();
        var initial = await table.ReadAllAsync(token);
        Assert.True(await table.InsertRowAsync(entry, initial.Version.Next(), token));
        var before = await table.ReadAllAsync(token);
        var absent = CreateMembershipEntryForTest();

        await table.UpdateIAmAliveAsync(absent, token);

        var after = await table.ReadAllAsync(token);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(entry.ToFullString(), Assert.Single(after.Members).Item1.ToFullString());
        Assert.Empty((await table.ReadRowAsync(absent.SiloAddress, token)).Members);
    }

    [Theory]
    [InlineData(false, SiloStatus.Active)]
    [InlineData(false, SiloStatus.Dead)]
    [InlineData(true, SiloStatus.Active)]
    [InlineData(true, SiloStatus.Dead)]
    public async Task MembershipTable_ConcurrentHeartbeatsAndFullRowUpdate_PreserveMaximum(bool ttl, SiloStatus status)
    {
        var token = TestContext.Current.CancellationToken;
        var (table, _) = await CreateNewMembershipTableAsync(token, cassandraTtl: ttl);
        var entry = CreateMembershipEntryForTest();
        entry.Status = status;
        var initial = await table.ReadAllAsync(token);
        Assert.True(await table.InsertRowAsync(entry, initial.Version.Next(), token));
        var before = await table.ReadRowAsync(entry.SiloAddress, token);
        var update = Assert.Single(before.Members);
        update.Item1.HostName += "-updated";
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeats = Enumerable.Range(1, 8).Reverse().Select(async offset =>
        {
            await start.Task.WaitAsync(token);
            await table.UpdateIAmAliveAsync(new MembershipEntry
            {
                SiloAddress = entry.SiloAddress,
                IAmAliveTime = entry.IAmAliveTime.AddSeconds(offset)
            }, token);
        }).ToArray();
        var fullRowWrite = UpdateRowAsync();
        start.SetResult();

        await Task.WhenAll(heartbeats.Append(fullRowWrite));
        Assert.True(await fullRowWrite);
        var after = await table.ReadRowAsync(entry.SiloAddress, token);
        var result = Assert.Single(after.Members).Item1;
        Assert.Equal(entry.IAmAliveTime.AddSeconds(8), result.IAmAliveTime);
        Assert.Equal(update.Item1.HostName, result.HostName);
        Assert.Equal(entry.StartTime, result.StartTime);
        Assert.Equal(status, result.Status);
        Assert.Equal(before.Version.Version + 1, after.Version.Version);

        async Task<bool> UpdateRowAsync()
        {
            await start.Task.WaitAsync(token);
            return await table.UpdateRowAsync(update.Item1, update.Item2, before.Version.Next(), token);
        }
    }

    [Fact]
    public async Task MembershipTable_FullRowUpdate_PreservesNewerHeartbeat()
    {
        var token = TestContext.Current.CancellationToken;
        var (table, _) = await CreateNewMembershipTableAsync(token);
        var entry = CreateMembershipEntryForTest();
        var initial = await table.ReadAllAsync(token);
        Assert.True(await table.InsertRowAsync(entry, initial.Version.Next(), token));
        var before = await table.ReadRowAsync(entry.SiloAddress, token);
        var stale = Assert.Single(before.Members);
        var heartbeat = entry.IAmAliveTime.AddMinutes(1);
        await table.UpdateIAmAliveAsync(new MembershipEntry
        {
            SiloAddress = entry.SiloAddress,
            IAmAliveTime = heartbeat
        }, token);
        stale.Item1.Status = SiloStatus.Dead;
        Assert.True(await table.UpdateRowAsync(stale.Item1, stale.Item2, before.Version.Next(), token));
        var after = await table.ReadRowAsync(entry.SiloAddress, token);
        Assert.Equal(heartbeat, Assert.Single(after.Members).Item1.IAmAliveTime);
        Assert.Equal(SiloStatus.Dead, after.Members[0].Item1.Status);
        Assert.Equal(before.Version.Version + 1, after.Version.Version);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("start")]
    [InlineData("heartbeat")]
    [InlineData("vote")]
    [InlineData("silo_name")]
    [InlineData("host_name")]
    [InlineData("proxy_port")]
    public async Task MembershipTable_Cleanup_CapturedRowProtectsConcurrentUpdates(string field)
    {
        var token = TestContext.Current.CancellationToken;
        var serviceId = $"Service_{Guid.NewGuid():N}";
        var clusterId = $"Cluster_{Guid.NewGuid():N}";
        var (table, _) = await CreateNewMembershipTableAsync(serviceId, clusterId, token);
        var entry = CreateMembershipEntryForTest();
        entry.Status = SiloStatus.Dead;
        entry.SuspectTimes = [];
        var initial = await table.ReadAllAsync(token);
        Assert.True(await table.InsertRowAsync(entry, initial.Version.Next(), token));
        var captured = Assert.Single((await table.ReadRowAsync(entry.SiloAddress, token)).Members).Item1;
        var current = await table.ReadRowAsync(entry.SiloAddress, token);
        var updated = Assert.Single(current.Members);
        switch (field)
        {
            case "status":
                updated.Item1.Status = SiloStatus.Active;
                break;
            case "start":
                updated.Item1.StartTime = updated.Item1.StartTime.AddMinutes(1);
                break;
            case "heartbeat":
                updated.Item1.IAmAliveTime = updated.Item1.IAmAliveTime.AddMinutes(1);
                break;
            case "vote":
                updated.Item1.SuspectTimes = [Tuple.Create(CreateSiloAddressForTest(), updated.Item1.IAmAliveTime.AddMinutes(1))];
                break;
            case "silo_name":
                updated.Item1.SiloName += "-updated";
                break;
            case "host_name":
                updated.Item1.HostName += "-updated";
                break;
            case "proxy_port":
                updated.Item1.ProxyPort++;
                break;
        }

        if (field == "heartbeat")
        {
            await table.UpdateIAmAliveAsync(updated.Item1, token);
        }
        else
        {
            Assert.True(await table.UpdateRowAsync(updated.Item1, updated.Item2, current.Version.Next(), token));
        }

        var beforeCleanup = await table.ReadRowAsync(entry.SiloAddress, token);
        var queries = await OrleansQueries.CreateInstance(await CreateSession(token));
        var result = await queries.ExecuteAsync(
            await queries.DeleteMembershipEntry($"{serviceId}-{clusterId}", captured, token), token);
        Assert.False((bool)result.First()["[applied]"]);
        var afterCleanup = await table.ReadRowAsync(entry.SiloAddress, token);
        Assert.Equal(beforeCleanup.Version, afterCleanup.Version);
        Assert.Equal(updated.Item1.ToFullString(), Assert.Single(afterCleanup.Members).Item1.ToFullString());
        Assert.Equal(updated.Item1.SiloName, afterCleanup.Members[0].Item1.SiloName);
        Assert.Equal(updated.Item1.HostName, afterCleanup.Members[0].Item1.HostName);
        Assert.Equal(updated.Item1.ProxyPort, afterCleanup.Members[0].Item1.ProxyPort);

        if (updated.Item1.Status == SiloStatus.Dead)
        {
            result = await queries.ExecuteAsync(
                await queries.DeleteMembershipEntry($"{serviceId}-{clusterId}", afterCleanup.Members[0].Item1, token), token);
            Assert.True((bool)result.First()["[applied]"]);
            var retired = await table.ReadRowAsync(entry.SiloAddress, token);
            Assert.Empty(retired.Members);
            Assert.Equal(afterCleanup.Version, retired.Version);
        }
    }

    [Fact]
    public async Task MembershipTable_CleanupDefunctSiloEntries()
    {
        var (membershipTable, _) = await CreateNewMembershipTableAsync(
            TestContext.Current.CancellationToken);

        var data = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);
        _testOutputHelper.WriteLine("Membership.ReadAll returned TableVersion={0} Data={1}", data.Version, data);

        Assert.Empty(data.Members);

        var newTableVersion = data.Version.Next();

        var oldEntryDead = CreateMembershipEntryForTest();
        oldEntryDead.IAmAliveTime = oldEntryDead.IAmAliveTime.AddDays(-10);
        oldEntryDead.StartTime = oldEntryDead.StartTime.AddDays(-10);
        oldEntryDead.Status = SiloStatus.Dead;
        var ok = await membershipTable.InsertRowAsync(oldEntryDead, newTableVersion, TestContext.Current.CancellationToken);
        var table = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);

        Assert.True(ok, "InsertRow Dead failed");

        newTableVersion = table.Version.Next();
        var oldEntryJoining = CreateMembershipEntryForTest();
        oldEntryJoining.IAmAliveTime = oldEntryJoining.IAmAliveTime.AddDays(-10);
        oldEntryJoining.StartTime = oldEntryJoining.StartTime.AddDays(-10);
        oldEntryJoining.Status = SiloStatus.Joining;
        ok = await membershipTable.InsertRowAsync(oldEntryJoining, newTableVersion, TestContext.Current.CancellationToken);
        table = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);

        Assert.True(ok, "InsertRow Joining failed");

        newTableVersion = table.Version.Next();
        var newEntry = CreateMembershipEntryForTest();
        ok = await membershipTable.InsertRowAsync(newEntry, newTableVersion, TestContext.Current.CancellationToken);
        Assert.True(ok, "InsertRow failed");

        data = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);
        newTableVersion = data.Version.Next();
        _testOutputHelper.WriteLine("Membership.ReadAll returned TableVersion={0} Data={1}", data.Version, data);

        Assert.Equal(3, data.Members.Count);

        // Compaction retains every non-Dead row in the versioned view.
        foreach (var siloStatus in Enum.GetValues<SiloStatus>().Where(status => status != SiloStatus.None))
        {
            var oldEntry = CreateMembershipEntryForTest();
            oldEntry.IAmAliveTime = oldEntry.IAmAliveTime.AddDays(-10);
            oldEntry.StartTime = oldEntry.StartTime.AddDays(-10);
            oldEntry.Status = siloStatus;
            ok = await membershipTable.InsertRowAsync(oldEntry, newTableVersion, TestContext.Current.CancellationToken);
            table = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);

            Assert.True(ok, "InsertRow failed");

            newTableVersion = table.Version.Next();
        }

        var beforeCleanup = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);
        var expected = beforeCleanup.Members.Where(row => row.Item1.Status != SiloStatus.Dead)
            .OrderBy(row => row.Item1.SiloAddress).ToList();
        await membershipTable.CleanupDefunctSiloEntriesAsync(oldEntryDead.IAmAliveTime.AddDays(3), TestContext.Current.CancellationToken);

        data = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);
        _testOutputHelper.WriteLine("Membership.ReadAll returned TableVersion={0} Data={1}", data.Version, data);

        Assert.Equal(expected.Count, data.Members.Count);
        Assert.Equal(beforeCleanup.Version, data.Version);
        Assert.Equal(
            expected.Select(row => row.Item1.ToFullString()),
            data.Members.OrderBy(row => row.Item1.SiloAddress).Select(row => row.Item1.ToFullString()));
        await membershipTable.CleanupDefunctSiloEntriesAsync(oldEntryDead.IAmAliveTime.AddDays(3), TestContext.Current.CancellationToken);
        var repeated = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(data.Version, repeated.Version);
        Assert.Equal(
            data.Members.OrderBy(row => row.Item1.SiloAddress).Select(row => (row.Item1.ToFullString(), row.Item2)),
            repeated.Members.OrderBy(row => row.Item1.SiloAddress).Select(row => (row.Item1.ToFullString(), row.Item2)));
    }

    // Utility methods
    private static MembershipEntry CreateMembershipEntryForTest()
    {
        var siloAddress = CreateSiloAddressForTest();

        var membershipEntry = new MembershipEntry
        {
            SiloAddress = siloAddress,
            HostName = HostName,
            SiloName = "TestSiloName",
            Status = SiloStatus.Joining,
            ProxyPort = siloAddress.Endpoint.Port,
            StartTime = GetUtcNowWithSecondsResolution(),
            IAmAliveTime = GetUtcNowWithSecondsResolution()
        };

        return membershipEntry;
    }

    private static DateTime GetUtcNowWithSecondsResolution()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Utc);
    }

    private static SiloAddress CreateSiloAddressForTest()
    {
        var siloAddress = SiloAddressUtils.NewLocalSiloAddress(Interlocked.Increment(ref _generation));
        siloAddress.Endpoint.Port = 12345;
        return siloAddress;
    }

    private async Task<(IMembershipTable, IGatewayListProvider)> CreateNewMembershipTableAsync(
        string serviceId,
        string clusterId,
        CancellationToken cancellationToken,
        bool cassandraTtl = false)
    {
        var services = CreateMembershipServices(serviceId, clusterId, () => CreateSession(cancellationToken), cassandraTtl);
        IMembershipTable membershipTable = services.GetRequiredService<CassandraClusteringTable>();
        await membershipTable.InitializeMembershipTableAsync(true, cancellationToken);

        IGatewayListProvider gatewayProvider = services.GetRequiredService<CassandraGatewayListProvider>();
        await gatewayProvider.InitializeGatewayListProvider();

        return (membershipTable, gatewayProvider);
    }

    private static ServiceProvider CreateMembershipServices(
        string serviceId,
        string clusterId,
        Func<Task<ISession>> createSession,
        bool cassandraTtl)
        => new ServiceCollection()
            .AddSingleton<CassandraClusteringTable>()
            .AddSingleton<CassandraGatewayListProvider>()
            .Configure<ClusterOptions>(o => { o.ServiceId = serviceId; o.ClusterId = clusterId; })
            .Configure<CassandraClusteringOptions>(o =>
            {
                o.ConfigureClient(_ => createSession());
                o.UseCassandraTtl = cassandraTtl;
            })
            .Configure<ClusterMembershipOptions>(o => o.DefunctSiloExpiration = TimeSpan.FromSeconds(20))
            .Configure<GatewayOptions>(o => o.GatewayListRefreshPeriod = TimeSpan.FromSeconds(15))
            .BuildServiceProvider();

    private async Task<ISession> CreateSession(CancellationToken cancellationToken)
    {
        var container = await _cassandraContainer.RunImage(cancellationToken);

        return container.session;
    }

    private Task<(IMembershipTable, IGatewayListProvider)> CreateNewMembershipTableAsync(
        CancellationToken cancellationToken,
        bool cassandraTtl = false)
    {
        var serviceId = $"Service_{Guid.NewGuid()}";
        var clusterId = $"Cluster_{Guid.NewGuid()}";

        return CreateNewMembershipTableAsync(
            serviceId,
            clusterId,
            cancellationToken,
            cassandraTtl);
    }

    [Fact]
    public async Task A_Test()
    {
        var serviceId = $"Service_{Guid.NewGuid()}";
        var clusterId = $"Cluster_{Guid.NewGuid()}";
        var clusterOptions = new ClusterOptions { ServiceId = serviceId, ClusterId = clusterId + "_1" };
        var clusterIdentifier = clusterOptions.ServiceId + "-" + clusterOptions.ClusterId;
        var (membershipTable, gatewayProvider) = await CreateNewMembershipTableAsync(
            serviceId,
            clusterId + "_1",
            TestContext.Current.CancellationToken);

        var (otherMembershipTable, _) = await CreateNewMembershipTableAsync(
            serviceId,
            clusterId + "_2",
            TestContext.Current.CancellationToken);

        var tableData = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);

        await membershipTable.InsertRowAsync(
            new MembershipEntry
            {
                HostName = "host1",
                IAmAliveTime = DateTime.UtcNow,
                ProxyPort = 2345,
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 2345, 1),
                SiloName = "silo1",
                Status = SiloStatus.Created,
                StartTime = DateTime.UtcNow
            }, tableData.Version.Next(), TestContext.Current.CancellationToken);

        tableData = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);

        await membershipTable.InsertRowAsync(
            new MembershipEntry
            {
                HostName = "host1",
                IAmAliveTime = DateTime.UtcNow,
                ProxyPort = 2345,
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 2345, 1),
                SiloName = "silo1",
                Status = SiloStatus.Joining,
                StartTime = DateTime.UtcNow
            }, tableData.Version.Next(), TestContext.Current.CancellationToken);

        tableData = await otherMembershipTable.ReadAllAsync(TestContext.Current.CancellationToken);
        await otherMembershipTable.InsertRowAsync(
            new MembershipEntry
            {
                HostName = "host1",
                IAmAliveTime = DateTime.UtcNow,
                ProxyPort = 2345,
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 2345, 1),
                SiloName = "silo1",
                Status = SiloStatus.Joining,
                StartTime = DateTime.UtcNow
            }, tableData.Version.Next(), TestContext.Current.CancellationToken);

        tableData = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);

        var membershipEntry = new MembershipEntry
        {
            HostName = "host1",
            IAmAliveTime = DateTime.UtcNow,
            ProxyPort = 2345,
            SiloAddress = SiloAddress.New(IPAddress.Loopback, 2346, 1),
            SiloName = "silo1",
            Status = SiloStatus.Active,
            StartTime = DateTime.UtcNow
        };
        await membershipTable.InsertRowAsync(membershipEntry, tableData.Version.Next(), TestContext.Current.CancellationToken);

        var readAll = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);

        _testOutputHelper.WriteLine(readAll.Version.Version.ToString());
        foreach (var row in readAll.Members)
        {
            var entry = row.Item1;
            _testOutputHelper.WriteLine(clusterIdentifier);
            _testOutputHelper.WriteLine("  " + entry.HostName);
            _testOutputHelper.WriteLine("  " + entry.SiloName);
            _testOutputHelper.WriteLine("  " + entry.StartTime);
            _testOutputHelper.WriteLine("  " + entry.IAmAliveTime);
            _testOutputHelper.WriteLine("  " + entry.SiloAddress);
            _testOutputHelper.WriteLine("  " + entry.ProxyPort);
            _testOutputHelper.WriteLine("  " + entry.Status);
        }

        membershipEntry.IAmAliveTime = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        await membershipTable.UpdateIAmAliveAsync(membershipEntry, TestContext.Current.CancellationToken);

        readAll = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);

        _testOutputHelper.WriteLine(readAll.Version.Version.ToString());
        foreach (var row in readAll.Members)
        {
            var entry = row.Item1;
            _testOutputHelper.WriteLine(clusterIdentifier);
            _testOutputHelper.WriteLine("  " + entry.HostName);
            _testOutputHelper.WriteLine("  " + entry.SiloName);
            _testOutputHelper.WriteLine("  " + entry.StartTime);
            _testOutputHelper.WriteLine("  " + entry.IAmAliveTime);
            _testOutputHelper.WriteLine("  " + entry.SiloAddress);
            _testOutputHelper.WriteLine("  " + entry.ProxyPort);
            _testOutputHelper.WriteLine("  " + entry.Status);
        }

        await gatewayProvider.InitializeGatewayListProvider();

        _ = await gatewayProvider.GetGateways();
        var gateways = await gatewayProvider.GetGateways();

        foreach (var gateway in gateways)
        {
            _testOutputHelper.WriteLine(gateway.ToString());
        }

        var queriedEntry = await membershipTable.ReadRowAsync(membershipEntry.SiloAddress, TestContext.Current.CancellationToken);
        foreach (var queriedEntryMember in queriedEntry.Members)
        {
            _testOutputHelper.WriteLine(queriedEntryMember.Item1.SiloAddress.ToParsableString());
        }

        await membershipTable.DeleteMembershipTableEntriesAsync(clusterOptions.ClusterId, TestContext.Current.CancellationToken);

        readAll = await membershipTable.ReadAllAsync(TestContext.Current.CancellationToken);

        _testOutputHelper.WriteLine(readAll.Version.Version.ToString());
        foreach (var row in readAll.Members)
        {
            var entry = row.Item1;
            _testOutputHelper.WriteLine(clusterIdentifier);
            _testOutputHelper.WriteLine("  " + entry.HostName);
            _testOutputHelper.WriteLine("  " + entry.SiloName);
            _testOutputHelper.WriteLine("  " + entry.StartTime);
            _testOutputHelper.WriteLine("  " + entry.IAmAliveTime);
            _testOutputHelper.WriteLine("  " + entry.SiloAddress);
            _testOutputHelper.WriteLine("  " + entry.ProxyPort);
            _testOutputHelper.WriteLine("  " + entry.Status);
        }
    }

}
