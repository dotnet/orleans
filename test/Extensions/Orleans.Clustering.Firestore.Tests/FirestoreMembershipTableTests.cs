using System.Net;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnitTests;
using TestExtensions;
using Orleans.Configuration;
using Orleans.Messaging;
using UnitTests.MembershipTests;
using Orleans.Clustering.Firestore;

namespace Orleans.Clustering.Firestore.Tests;

[TestSuite("Functional")]
[TestProvider("GoogleCloud")]
[TestCategory("Functional"), TestCategory("Firestore"), TestCategory("GoogleCloud")]
public class FirestoreMembershipTableTests : MembershipTableTestsBase, IClassFixture<TestEnvironmentFixture>
{
    public FirestoreMembershipTableTests(
        ConnectionStringFixture csFixture,
        TestEnvironmentFixture environment) : base(csFixture, environment, CreateFilters())
    {
    }

    private static LoggerFilterOptions CreateFilters()
    {
        var filters = new LoggerFilterOptions();
        filters.AddFilter("FirestoreDataManager", LogLevel.Trace);
        filters.AddFilter("Storage", LogLevel.Trace);
        filters.AddFilter("FirestoreMembershipTable", LogLevel.Trace);
        return filters;
    }

    protected override IMembershipTable CreateMembershipTable(ILogger logger)
    {
        var options = new FirestoreOptions
        {
            ProjectId = "orleans-test",
            EmulatorHost = GoogleEmulatorHost.FirestoreEndpoint
        };

        return new FirestoreMembershipTable(this.loggerFactory, Options.Create(options), this._clusterOptions);
    }

    protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
    {
        var options = new FirestoreOptions
        {
            ProjectId = GoogleEmulatorHost.ProjectId,
            EmulatorHost = GoogleEmulatorHost.FirestoreEndpoint
        };

        return new FirestoreGatewayListProvider(this.loggerFactory, Options.Create(options), this._clusterOptions, this._gatewayOptions);
    }

    protected override Task<string> GetConnectionString() => Task.FromResult("<dummy>");

    [SkippableFact]
    public Task GetGateways() => MembershipTable_GetGateways();

    [SkippableFact]
    public Task ReadAll_EmptyTable() => MembershipTable_ReadAll_EmptyTable();

    [SkippableFact]
    public Task InsertRow() => MembershipTable_InsertRow();

    [SkippableFact]
    public Task ReadRow_Insert_Read() => MembershipTable_ReadRow_Insert_Read();

    [SkippableFact]
    public Task ReadAll_Insert_ReadAll() => MembershipTable_ReadAll_Insert_ReadAll();

    [SkippableFact]
    public Task UpdateRow() => MembershipTable_UpdateRow();

    [SkippableFact]
    public Task CleanupDefunctSiloEntries() => MembershipTable_CleanupDefunctSiloEntries();

    [SkippableFact]
    public Task UpdateRowInParallel() => MembershipTable_UpdateRowInParallel();

    [SkippableFact]
    public Task UpdateIAmAlive() => MembershipTable_UpdateIAmAlive();

    [SkippableFact]
    public async Task MembershipReadsReturnAtomicSnapshotsDuringConcurrentUpdates()
    {
        const int fillerCount = 16;
        const int updateCount = 20;
        const int readsPerReader = 30;
        var clusterId = $"snapshot-{Guid.NewGuid():N}";
        var options = new FirestoreOptions
        {
            ProjectId = GoogleEmulatorHost.ProjectId,
            EmulatorHost = GoogleEmulatorHost.FirestoreEndpoint,
        };
        var table = new FirestoreMembershipTable(
            this.loggerFactory,
            Options.Create(options),
            Options.Create(new ClusterOptions { ClusterId = clusterId }));

        await table.InitializeMembershipTable(true);
        try
        {
            var initial = await table.ReadAll();
            var address = SiloAddress.New(IPAddress.Loopback, 12_000, 1);
            var version = initial.Version.Next();
            var entry = CreateMembershipEntry(address, version.Version);
            Assert.True(await table.InsertRow(entry, version));

            // Make ReadAll consume a multi-message RunQuery stream while the sentinel row changes.
            var storage = new FirestoreDataManager(
                "Cluster",
                Utils.SanitizeId(clusterId),
                options,
                this.loggerFactory.CreateLogger<FirestoreDataManager>());
            var fillers = Enumerable.Range(0, fillerCount)
                .Select(index => SiloInstanceEntity.FromMembershipEntry(
                    CreateMembershipEntry(
                        SiloAddress.New(IPAddress.Loopback, 12_001 + index, index + 2),
                        proxyPort: 30_000),
                    membershipVersion: 0))
                .ToArray();
            await Task.WhenAll(fillers.Select(filler => storage.UpsertEntity(filler)));

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writer = WriteUpdates();
            var readAll = ReadSnapshots(table.ReadAll);
            var readRow = ReadSnapshots(() => table.ReadRow(address));
            start.SetResult();

            await Task.WhenAll(writer, readAll, readRow).WaitAsync(TimeSpan.FromMinutes(2));

            async Task WriteUpdates()
            {
                await start.Task;
                for (var i = 0; i < updateCount; i++)
                {
                    var updated = false;
                    while (!updated)
                    {
                        var snapshot = await ReadWithRetries(() => table.ReadRow(address));
                        var row = Assert.Single(snapshot.Members);
                        var nextVersion = snapshot.Version.Next();
                        row.Item1.ProxyPort = nextVersion.Version;
                        updated = await table.UpdateRow(row.Item1, row.Item2, nextVersion);
                    }
                }

            }

            async Task ReadSnapshots(Func<Task<MembershipTableData>> read)
            {
                await start.Task;
                var previousVersion = -1;
                for (var i = 0; i < readsPerReader; i++)
                {
                    var snapshot = await ReadWithRetries(read);
                    Assert.True(snapshot.Version.Version >= previousVersion);
                    var row = Assert.Single(
                        snapshot.Members,
                        member => member.Item1.SiloAddress.Equals(address));
                    Assert.Equal(snapshot.Version.Version, row.Item1.ProxyPort);
                    previousVersion = snapshot.Version.Version;
                }
            }

            static async Task<MembershipTableData> ReadWithRetries(Func<Task<MembershipTableData>> read)
            {
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        return await read();
                    }
                    catch (RpcException exception) when (
                        exception.StatusCode == StatusCode.Aborted && attempt < 9)
                    {
                        await Task.Delay(10);
                    }
                }
            }
        }
        finally
        {
            await table.DeleteMembershipTableEntries(clusterId);
        }
    }

    private static MembershipEntry CreateMembershipEntry(SiloAddress address, int proxyPort)
    {
        var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return new MembershipEntry
        {
            SiloAddress = address,
            HostName = "localhost",
            SiloName = $"Silo-{address.Generation}",
            Status = SiloStatus.Active,
            ProxyPort = proxyPort,
            StartTime = timestamp,
            IAmAliveTime = timestamp,
        };
    }
}