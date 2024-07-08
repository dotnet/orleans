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

[TestCategory("Cassandra"), TestCategory("Clustering")]
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

    [Fact]
    public async Task MembershipTable_UpdateIAmAlive()
    {
        var (membershipTable, _) = await CreateNewMembershipTableAsync();

        var tableData = await membershipTable.ReadAll();

        var newTableVersion = tableData.Version.Next();
        var newEntry = CreateMembershipEntryForTest();
        var ok = await membershipTable.InsertRow(newEntry, newTableVersion);
        Assert.True(ok);

        var amAliveTime = DateTime.UtcNow.Add(TimeSpan.FromSeconds(5));

        // This mimics the arguments MembershipOracle.OnIAmAliveUpdateInTableTimer passes in
        var entry = new MembershipEntry
        {
            SiloAddress = newEntry.SiloAddress,
            IAmAliveTime = amAliveTime
        };

        await membershipTable.UpdateIAmAlive(entry);

        tableData = await membershipTable.ReadAll();
        var member = tableData.Members.First(e => e.Item1.SiloAddress.Equals(newEntry.SiloAddress));

        // compare that the value is close to what we passed in, but not exactly, as the underlying store can set its own precision settings
        // (ie: in SQL Server this is defined as datetime2(3), so we don't expect precision to account for less than 0.001s values)
        Assert.True((amAliveTime - member.Item1.IAmAliveTime).Duration() < TimeSpan.FromSeconds(2), "Expected time around " + amAliveTime + " but got " + member.Item1.IAmAliveTime + " that is off by " + (amAliveTime - member.Item1.IAmAliveTime).Duration().ToString());
        Assert.Equal(newTableVersion.Version, tableData.Version.Version);
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

    private async Task<(IMembershipTable, IGatewayListProvider)> CreateNewMembershipTableAsync(string serviceId, string clusterId)
    {
        var session = await CreateSession();

        var services = new ServiceCollection()
            .AddSingleton<CassandraClusteringTable>()
            .AddSingleton<CassandraGatewayListProvider>()
            .Configure<ClusterOptions>(o => { o.ServiceId = serviceId; o.ClusterId = clusterId; })
            .Configure<CassandraClusteringOptions>(o => o.ConfigureClient(async _ => await CreateSession()))
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

    private Task<(IMembershipTable, IGatewayListProvider)> CreateNewMembershipTableAsync()
    {
        var serviceId = $"Service_{Guid.NewGuid()}";
        var clusterId = $"Cluster_{Guid.NewGuid()}";

        return CreateNewMembershipTableAsync(serviceId, clusterId);
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
