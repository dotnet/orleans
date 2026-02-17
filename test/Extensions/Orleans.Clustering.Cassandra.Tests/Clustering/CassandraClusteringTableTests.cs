using System.Net;
using Cassandra;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Clustering.Cassandra;
using Orleans.Clustering.Cassandra.Hosting;
using Orleans.Configuration;
using Orleans.Messaging;
using Tester.Cassandra.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Tester.Cassandra.Clustering;

/// <summary>
/// Tests for Orleans membership table operations using Apache Cassandra as the backing store.
/// </summary>
[TestCategory("Cassandra"), TestCategory("Clustering")]
[Collection("Cassandra")]
public sealed class CassandraClusteringTableTests : IClassFixture<CassandraContainer>
{
    private readonly CassandraContainer _cassandraContainer;
    private readonly ITestOutputHelper _testOutputHelper;
    private static readonly string HostName = Dns.GetHostName();
    private static int _generation;

    public CassandraClusteringTableTests(CassandraContainer cassandraContainer, ITestOutputHelper testOutputHelper)
    {
        _cassandraContainer = cassandraContainer;
        _cassandraContainer.Name = nameof(Cassandra);
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public async Task MembershipTable_GetGateways()
    {
        var (membershipTable, gatewayListProvider) = await CreateNewMembershipTableAsync();

        var membershipEntries = Enumerable.Range(0, 10).Select(_ => CreateMembershipEntryForTest()).ToArray();

        membershipEntries[3].Status = SiloStatus.Active;
        membershipEntries[3].ProxyPort = 0;
        membershipEntries[5].Status = SiloStatus.Active;
        membershipEntries[9].Status = SiloStatus.Active;

        var data = await membershipTable.ReadAll();
        Assert.NotNull(data);
        Assert.Empty(data.Members);

        var version = data.Version;
        foreach (var membershipEntry in membershipEntries)
        {
            Assert.True(await membershipTable.InsertRow(membershipEntry, version.Next()));
            version = (await membershipTable.ReadRow(membershipEntry.SiloAddress)).Version;
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
        var (membershipTable, _) = await CreateNewMembershipTableAsync();

        var data = await membershipTable.ReadAll();
        Assert.NotNull(data);

        _testOutputHelper.WriteLine("Membership.ReadAll returned TableVersion={0} Data={1}", data.Version, data);

        Assert.Empty(data.Members);
        Assert.NotNull(data.Version.VersionEtag);
        Assert.Equal(0, data.Version.Version);
    }

    [Fact]
    public async Task MembershipTable_InsertRow()
    {
        var (membershipTable, _) = await CreateNewMembershipTableAsync();

        var membershipEntry = CreateMembershipEntryForTest();

        var data = await membershipTable.ReadAll();
        Assert.NotNull(data);
        Assert.Empty(data.Members);

        var nextTableVersion = data.Version.Next();

        var ok = await membershipTable.InsertRow(membershipEntry, nextTableVersion);
        Assert.True(ok, "InsertRow failed");

        data = await membershipTable.ReadAll();

        Assert.Equal(1, data.Version.Version);

        Assert.Single(data.Members);
    }

    [Fact]
    public async Task MembershipTable_ReadRow_Insert_Read()
    {
        var (membershipTable, gatewayProvider) = await CreateNewMembershipTableAsync("Phalanx", "blu");

        MembershipTableData data = await membershipTable.ReadAll();

        _testOutputHelper.WriteLine("Membership.ReadAll returned TableVersion={0} Data={1}", data.Version, data);

        Assert.Empty(data.Members);

        TableVersion newTableVersion = data.Version.Next();

        MembershipEntry newEntry = CreateMembershipEntryForTest();
        bool ok = await membershipTable.InsertRow(newEntry, newTableVersion);
        Assert.True(ok, "InsertRow failed");

        ok = await membershipTable.InsertRow(newEntry, newTableVersion);
        Assert.False(ok, "InsertRow should have failed - same entry, old table version");

        ok = await membershipTable.InsertRow(CreateMembershipEntryForTest(), newTableVersion);
        Assert.False(ok, "InsertRow should have failed - new entry, old table version");

        data = await membershipTable.ReadAll();

        Assert.Equal(1, data.Version.Version);

        TableVersion nextTableVersion = data.Version.Next();

        ok = await membershipTable.InsertRow(newEntry, nextTableVersion);
        Assert.False(ok, "InsertRow should have failed - duplicate entry");

        data = await membershipTable.ReadAll();
        Assert.Single(data.Members);

        data = await membershipTable.ReadRow(newEntry.SiloAddress);
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
        var (membershipTable, _) = await CreateNewMembershipTableAsync();

        var data = await membershipTable.ReadAll();
        _testOutputHelper.WriteLine("Membership.ReadAll returned TableVersion={0} Data={1}", data.Version, data);

        Assert.Empty(data.Members);

        var newTableVersion = data.Version.Next();

        var newEntry = CreateMembershipEntryForTest();
        var ok = await membershipTable.InsertRow(newEntry, newTableVersion);
        Assert.True(ok, "InsertRow failed");

        data = await membershipTable.ReadAll();
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
        var (membershipTable, _) = await CreateNewMembershipTableAsync();

        var tableData = await membershipTable.ReadAll();
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

            var tableVersion = tableData.Version.Next();

            _testOutputHelper.WriteLine("Calling InsertRow with Entry = {0} TableVersion = {1}", siloEntry, tableVersion);
            var ok = await membershipTable.InsertRow(siloEntry, tableVersion);
            Assert.True(ok, "InsertRow failed");

            tableData = await membershipTable.ReadAll();

            var etagBefore = tableData.TryGet(siloEntry.SiloAddress)?.Item2;

            Assert.NotNull(etagBefore);

            _testOutputHelper.WriteLine(
                "Calling UpdateRow with Entry = {0} correct eTag = {1} old version={2}",
                siloEntry,
                etagBefore,
                tableVersion?.ToString() ?? "null");
            ok = await membershipTable.UpdateRow(siloEntry, etagBefore, tableVersion);
            Assert.False(ok, $"row update should have failed - Table Data = {tableData}");
            tableData = await membershipTable.ReadAll();

            tableVersion = tableData.Version.Next();

            _testOutputHelper.WriteLine(
                "Calling UpdateRow with Entry = {0} correct eTag = {1} correct version={2}",
                siloEntry,
                etagBefore,
                tableVersion?.ToString() ?? "null");

            ok = await membershipTable.UpdateRow(siloEntry, etagBefore, tableVersion);

            Assert.True(ok, $"UpdateRow failed - Table Data = {tableData}");

            _testOutputHelper.WriteLine(
                "Calling UpdateRow with Entry = {0} old eTag = {1} old version={2}",
                siloEntry,
                etagBefore,
                tableVersion?.ToString() ?? "null");
            ok = await membershipTable.UpdateRow(siloEntry, etagBefore, tableVersion);
            Assert.False(ok, $"row update should have failed - Table Data = {tableData}");

            tableData = await membershipTable.ReadAll();

            var tuple = tableData.TryGet(siloEntry.SiloAddress);

            Assert.Equal(tuple.Item1.ToFullString(), siloEntry.ToFullString());

            var etagAfter = tuple.Item2;

            _testOutputHelper.WriteLine(
                "Calling UpdateRow with Entry = {0} correct eTag = {1} old version={2}",
                siloEntry,
                etagAfter,
                tableVersion?.ToString() ?? "null");

            ok = await membershipTable.UpdateRow(siloEntry, etagAfter, tableVersion);

            Assert.False(ok, $"row update should have failed - Table Data = {tableData}");

            tableData = await membershipTable.ReadAll();

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
            tasks.Add(Task.Run(async () =>
            {
                await Task.Yield();
                var (membershipTable, _) = await CreateNewMembershipTableAsync();
            }));
        }
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task MembershipTable_UpdateRowInParallel()
    {
        var (membershipTable, _) = await CreateNewMembershipTableAsync();

        var tableData = await membershipTable.ReadAll();

        var data = CreateMembershipEntryForTest();

        var newTableVer = tableData.Version.Next();

        var insertions = Task.WhenAll(Enumerable.Range(1, 20).Select(async _ => { try { return await membershipTable.InsertRow(data, newTableVer); } catch { return false; } }));

        Assert.True((await insertions).Single(x => x), "InsertRow failed");

        await Task.WhenAll(Enumerable.Range(1, 19).Select(async _ =>
        {
            var done = false;
            do
            {
                var updatedTableData = await membershipTable.ReadAll();
                var updatedRow = updatedTableData.TryGet(data.SiloAddress);

                await Task.Delay(10);
                if (updatedRow is null) continue;

                var tableVersion = updatedTableData.Version.Next();
                try
                {
                    done = await membershipTable.UpdateRow(updatedRow.Item1, updatedRow.Item2, tableVersion);
                }
                catch
                {
                    done = false;
                }
            } while (!done);
        })).WithTimeout(TimeSpan.FromSeconds(30));


        tableData = await membershipTable.ReadAll();
        Assert.NotNull(tableData.Version);

        Assert.Equal(20, tableData.Version.Version);

        Assert.Single(tableData.Members);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MembershipTable_UpdateIAmAlive(bool cassandraTtl)
    {
        // Drop the membership table before starting because the TTL behavior depends on the table creation
        ISession ttlSession = await CreateSession();
        await ttlSession.ExecuteAsync(new SimpleStatement("DROP TABLE IF EXISTS membership;"));

        var (membershipTable, _) = await CreateNewMembershipTableAsync(cassandraTtl:cassandraTtl);

        var tableData = await membershipTable.ReadAll();

        var newTableVersion = tableData.Version.Next();
        var newEntry = CreateMembershipEntryForTest();
        var ok = await membershipTable.InsertRow(newEntry, newTableVersion);
        Assert.True(ok);
        MembershipEntry originalMembershipEntry = (await membershipTable.ReadAll())
            .Members.First(e => e.Item1.SiloAddress.Equals(newEntry.SiloAddress))
            .Item1;
        Assert.Null(originalMembershipEntry.SuspectTimes);

        // Validate initial TTL values
        var initialTtlValues = new Dictionary<string, int>();
        await ValidateTtlValues(initial:true);

        var amAliveTime = DateTime.UtcNow.Add(TimeSpan.FromSeconds(5));

        // This mimics the arguments MembershipOracle.OnIAmAliveUpdateInTableTimer passes in
        var entry = new MembershipEntry
        {
            SiloAddress = newEntry.SiloAddress,
            IAmAliveTime = amAliveTime
        };

        await membershipTable.UpdateIAmAlive(entry);

        tableData = await membershipTable.ReadAll();
        MembershipEntry updatedMember = tableData.Members
            .First(e => e.Item1.SiloAddress.Equals(newEntry.SiloAddress))
            .Item1;

        // compare that the value is close to what we passed in, but not exactly, as the underlying store can set its own precision settings
        // (ie: in SQL Server this is defined as datetime2(3), so we don't expect precision to account for less than 0.001s values)
        Assert.True(
            (amAliveTime - updatedMember.IAmAliveTime).Duration() < TimeSpan.FromSeconds(2),
            $"Expected time around {amAliveTime} but got {updatedMember.IAmAliveTime} that is off by {(amAliveTime - updatedMember.IAmAliveTime).Duration()}");
        Assert.Equal(newTableVersion.Version, tableData.Version.Version);

        // Validate the rest of the data is still the same after the update
        Assert.Equal(originalMembershipEntry.SiloAddress, updatedMember.SiloAddress);
        Assert.Equal(originalMembershipEntry.SiloName, updatedMember.SiloName);
        Assert.Equal(originalMembershipEntry.HostName, updatedMember.HostName);
        Assert.Equal(originalMembershipEntry.Status, updatedMember.Status);
        Assert.Equal(originalMembershipEntry.ProxyPort, updatedMember.ProxyPort);
        Assert.Null(updatedMember.SuspectTimes);
        Assert.Equal(originalMembershipEntry.StartTime, updatedMember.StartTime);

        // Validate the TTL values are greater than the initial values read after the delay
        await ValidateTtlValues(initial:false);

        // Validate data automatically expires when using Cassandra TTL, and is still present if not
        // The Cassandra TTL is set to 20 seconds for this testing
        using var cts = new CancellationTokenSource(delay:TimeSpan.FromSeconds(30));
        if (cassandraTtl)
        {
            await ValidateDataIsDeleted(cts.Token);
        }
        else
        {
            await ValidateDataIsNotDeleted(cts.Token);
        }

        return;

        async Task ValidateTtlValues(bool initial)
        {
            if (cassandraTtl && initial)
            {
                // When actually using the TTL, wait 5 seconds so the TTL values will be less than 20
                await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
            }

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
                    """)))
                .First();

            object versionTtl = ttlResult["version_tll"];
            object siloNameTtl = ttlResult["siloname_ttl"];
            object hostNameTtl = ttlResult["hostname_ttl"];
            object statusTtl = ttlResult["status_ttl"];
            object proxyPortTtl = ttlResult["proxyport_ttl"];
            object suspectTimesTtl = ttlResult["suspecttimes_ttl"];
            object startTimeTtl = ttlResult["starttime_ttl"];
            object iAmAliveTtl = ttlResult["iamalivetime_ttl"];

            if (cassandraTtl)
            {
                // TTLs should be non-null, and if not the initial TTL check, should be greater
                Assert.True(int.TryParse(versionTtl.ToString(), out int versionInt));
                Assert.True(int.TryParse(siloNameTtl.ToString(), out int siloNameInt));
                Assert.True(int.TryParse(hostNameTtl.ToString(), out int hostNameInt));
                Assert.True(int.TryParse(statusTtl.ToString(), out int statusInt));
                Assert.True(int.TryParse(proxyPortTtl.ToString(), out int proxyPortInt));
                Assert.True(int.TryParse(startTimeTtl.ToString(), out int startTimeInt));
                Assert.True(int.TryParse(iAmAliveTtl.ToString(), out int iAmAliveInt));
                if (initial)
                {
                    Assert.True(versionInt > 0);
                    Assert.True(siloNameInt > 0);
                    Assert.True(hostNameInt > 0);
                    Assert.True(statusInt > 0);
                    Assert.True(proxyPortInt > 0);
                    Assert.True(startTimeInt > 0);
                    Assert.True(iAmAliveInt > 0);

                    initialTtlValues["version_tll"] = versionInt;
                    initialTtlValues["siloname_ttl"] = siloNameInt;
                    initialTtlValues["hostname_ttl"] = hostNameInt;
                    initialTtlValues["status_ttl"] = statusInt;
                    initialTtlValues["proxyport_ttl"] = proxyPortInt;
                    initialTtlValues["starttime_ttl"] = startTimeInt;
                    initialTtlValues["iamalivetime_ttl"] = iAmAliveInt;
                }
                else
                {
                    Assert.True(versionInt > initialTtlValues["version_tll"]);
                    Assert.True(siloNameInt > initialTtlValues["siloname_ttl"]);
                    Assert.True(hostNameInt > initialTtlValues["hostname_ttl"]);
                    Assert.True(statusInt > initialTtlValues["status_ttl"]);
                    Assert.True(proxyPortInt > initialTtlValues["proxyport_ttl"]);
                    Assert.True(startTimeInt > initialTtlValues["starttime_ttl"]);
                    Assert.True(iAmAliveInt > initialTtlValues["iamalivetime_ttl"]);
                }

                // suspect times will always be null because we're not actually filing it out in the test
                Assert.Null(suspectTimesTtl);
            }
            else
            {
                // TTLs should always be null when Cassandra TTL is disabled (default_time_to_live is 0)
                Assert.Null(versionTtl);
                Assert.Null(siloNameTtl);
                Assert.Null(hostNameTtl);
                Assert.Null(statusTtl);
                Assert.Null(proxyPortTtl);
                Assert.Null(suspectTimesTtl);
                Assert.Null(startTimeTtl);
                Assert.Null(iAmAliveTtl);
            }
        }

        async Task ValidateDataIsDeleted(CancellationToken ct)
        {
            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    throw new TimeoutException("Did not validate Cassandra data deletion within timeout");
                }

                tableData = await membershipTable.ReadAll();
                if (tableData.Members.Count == 0)
                {
                    // Success!
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
            }
        }

        async Task ValidateDataIsNotDeleted(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                tableData = await membershipTable.ReadAll();
                if (tableData.Members.Count == 0)
                {
                    throw new Exception("Cassandra data was unexpectedly deleted when not using a TTL");
                }

                await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task MembershipTable_CleanupDefunctSiloEntries()
    {
        var (membershipTable, _) = await CreateNewMembershipTableAsync();

        var data = await membershipTable.ReadAll();
        _testOutputHelper.WriteLine("Membership.ReadAll returned TableVersion={0} Data={1}", data.Version, data);

        Assert.Empty(data.Members);

        var newTableVersion = data.Version.Next();

        var oldEntryDead = CreateMembershipEntryForTest();
        oldEntryDead.IAmAliveTime = oldEntryDead.IAmAliveTime.AddDays(-10);
        oldEntryDead.StartTime = oldEntryDead.StartTime.AddDays(-10);
        oldEntryDead.Status = SiloStatus.Dead;
        var ok = await membershipTable.InsertRow(oldEntryDead, newTableVersion);
        var table = await membershipTable.ReadAll();

        Assert.True(ok, "InsertRow Dead failed");

        newTableVersion = table.Version.Next();
        var oldEntryJoining = CreateMembershipEntryForTest();
        oldEntryJoining.IAmAliveTime = oldEntryJoining.IAmAliveTime.AddDays(-10);
        oldEntryJoining.StartTime = oldEntryJoining.StartTime.AddDays(-10);
        oldEntryJoining.Status = SiloStatus.Joining;
        ok = await membershipTable.InsertRow(oldEntryJoining, newTableVersion);
        table = await membershipTable.ReadAll();

        Assert.True(ok, "InsertRow Joining failed");

        newTableVersion = table.Version.Next();
        var newEntry = CreateMembershipEntryForTest();
        ok = await membershipTable.InsertRow(newEntry, newTableVersion);
        Assert.True(ok, "InsertRow failed");

        data = await membershipTable.ReadAll();
        newTableVersion = data.Version.Next();
        _testOutputHelper.WriteLine("Membership.ReadAll returned TableVersion={0} Data={1}", data.Version, data);

        Assert.Equal(3, data.Members.Count);

        // Every status other than Active should get cleared out if old
        foreach (var siloStatus in Enum.GetValues<SiloStatus>())
        {
            var oldEntry = CreateMembershipEntryForTest();
            oldEntry.IAmAliveTime = oldEntry.IAmAliveTime.AddDays(-10);
            oldEntry.StartTime = oldEntry.StartTime.AddDays(-10);
            oldEntry.Status = siloStatus;
            ok = await membershipTable.InsertRow(oldEntry, newTableVersion);
            table = await membershipTable.ReadAll();

            Assert.True(ok, "InsertRow failed");

            newTableVersion = table.Version.Next();
        }

        await membershipTable.CleanupDefunctSiloEntries(oldEntryDead.IAmAliveTime.AddDays(3));

        data = await membershipTable.ReadAll();
        _testOutputHelper.WriteLine("Membership.ReadAll returned TableVersion={0} Data={1}", data.Version, data);

        Assert.Equal(2, data.Members.Count);
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
        bool cassandraTtl = false)
    {
        var services = new ServiceCollection()
            .AddSingleton<CassandraClusteringTable>()
            .AddSingleton<CassandraGatewayListProvider>()
            .Configure<ClusterOptions>(o => { o.ServiceId = serviceId; o.ClusterId = clusterId; })
            .Configure<CassandraClusteringOptions>(o =>
            {
                o.ConfigureClient(async _ => await CreateSession());
                o.UseCassandraTtl = cassandraTtl;
            })
            .Configure<ClusterMembershipOptions>(o =>
            {
                if (cassandraTtl)
                {
                    // Shorten the Cassandra TTL period so we can more easily check that rows are automatically deleted
                    o.DefunctSiloExpiration = TimeSpan.FromSeconds(20);
                }
            })
            .Configure<GatewayOptions>(o => o.GatewayListRefreshPeriod = TimeSpan.FromSeconds(15))
            .BuildServiceProvider();
        IMembershipTable membershipTable = services.GetRequiredService<CassandraClusteringTable>();
        await membershipTable.InitializeMembershipTable(true);

        IGatewayListProvider gatewayProvider = services.GetRequiredService < CassandraGatewayListProvider>();
        await gatewayProvider.InitializeGatewayListProvider();

        return (membershipTable, gatewayProvider);
    }

    private async Task<ISession> CreateSession()
    {
        var container = await _cassandraContainer.RunImage();

        return container.session;
    }

    private Task<(IMembershipTable, IGatewayListProvider)> CreateNewMembershipTableAsync(bool cassandraTtl = false)
    {
        var serviceId = $"Service_{Guid.NewGuid()}";
        var clusterId = $"Cluster_{Guid.NewGuid()}";

        return CreateNewMembershipTableAsync(serviceId, clusterId, cassandraTtl);
    }

    [Fact]
    public async Task A_Test()
    {
        var serviceId = $"Service_{Guid.NewGuid()}";
        var clusterId = $"Cluster_{Guid.NewGuid()}";
        var clusterOptions = new ClusterOptions { ServiceId = serviceId, ClusterId = clusterId + "_1" };
        var clusterIdentifier = clusterOptions.ServiceId + "-" + clusterOptions.ClusterId;
        var (membershipTable, gatewayProvider) = await CreateNewMembershipTableAsync(serviceId, clusterId + "_1");

        var (otherMembershipTable, _) = await CreateNewMembershipTableAsync(serviceId, clusterId + "_2");

        var tableData = await membershipTable.ReadAll();

        await membershipTable.InsertRow(
            new MembershipEntry
            {
                HostName = "host1",
                IAmAliveTime = DateTime.UtcNow,
                ProxyPort = 2345,
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 2345, 1),
                SiloName = "silo1",
                Status = SiloStatus.Created,
                StartTime = DateTime.UtcNow
            }, tableData.Version.Next());

        tableData = await membershipTable.ReadAll();

        await membershipTable.InsertRow(
            new MembershipEntry
            {
                HostName = "host1",
                IAmAliveTime = DateTime.UtcNow,
                ProxyPort = 2345,
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 2345, 1),
                SiloName = "silo1",
                Status = SiloStatus.Joining,
                StartTime = DateTime.UtcNow
            }, tableData.Version.Next());

        tableData = await otherMembershipTable.ReadAll();
        await otherMembershipTable.InsertRow(
            new MembershipEntry
            {
                HostName = "host1",
                IAmAliveTime = DateTime.UtcNow,
                ProxyPort = 2345,
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 2345, 1),
                SiloName = "silo1",
                Status = SiloStatus.Joining,
                StartTime = DateTime.UtcNow
            }, tableData.Version.Next());

        tableData = await membershipTable.ReadAll();

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
        await membershipTable.InsertRow(membershipEntry, tableData.Version.Next());

        var readAll = await membershipTable.ReadAll();

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
        await membershipTable.UpdateIAmAlive(membershipEntry);

        readAll = await membershipTable.ReadAll();

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

        var queriedEntry = await membershipTable.ReadRow(membershipEntry.SiloAddress);
        foreach (var queriedEntryMember in queriedEntry.Members)
        {
            _testOutputHelper.WriteLine(queriedEntryMember.Item1.SiloAddress.ToParsableString());
        }

        await membershipTable.DeleteMembershipTableEntries(clusterOptions.ClusterId);

        readAll = await membershipTable.ReadAll();

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
