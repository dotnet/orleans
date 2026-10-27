using System.Globalization;
using System.Net;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.AzureUtils;
using Orleans.Clustering.AzureStorage;
using Orleans.Clustering.TestKit;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using TestExtensions;
using UnitTests;
using UnitTests.MembershipTests;
using Xunit;

namespace Tester.AzureUtils
{
    /// <summary>
    /// Tests for operation of Orleans Membership Table using AzureStore - Requires access to external Azure storage
    /// 
    /// Azure Table Storage provides a scalable, highly available membership table implementation for Orleans.
    /// Key features include:
    /// - Automatic partitioning and load balancing
    /// - Strong consistency guarantees within partitions
    /// - Built-in redundancy and disaster recovery
    /// - Integration with Azure monitoring and diagnostics
    /// 
    /// These tests verify all membership operations work correctly with Azure Table Storage,
    /// including concurrent updates, failure detection, and gateway discovery.
    /// </summary>
    [TestCategory("Membership"), TestCategory("AzureStorage")]
    [TestSuite("Functional")]
    [TestProvider("AzureStorage")]
    [TestArea("Membership")]
    public class AzureMembershipTableTests : MembershipTableTestsBase
    {
        public AzureMembershipTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, CreateFilters())
        {
            TestUtils.CheckForAzureStorage();
        }

        private static LoggerFilterOptions CreateFilters()
        {
            var filters = new LoggerFilterOptions();
            filters.AddFilter(typeof(Orleans.Clustering.AzureStorage.AzureTableDataManager<>).FullName, LogLevel.Trace);
            filters.AddFilter(typeof(OrleansSiloInstanceManager).FullName, LogLevel.Trace);
            filters.AddFilter("Orleans.Storage", LogLevel.Trace);
            return filters;
        }

        /// <summary>
        /// Creates an Azure Table Storage-based membership table for testing.
        /// Configures the table with test defaults including connection strings
        /// and table names suitable for unit testing.
        /// </summary>
        protected override IMembershipTable CreateMembershipTable(ILogger logger)
            => CreateMembershipTable(logger, _clusterOptions);

        protected override IMembershipTable CreateMembershipTable(ILogger logger, IOptions<ClusterOptions> clusterOptions)
        {
            TestUtils.CheckForAzureStorage();
            var options = new AzureStorageClusteringOptions();
            options.ConfigureTestDefaults();
            return new AzureBasedMembershipTable(loggerFactory, Options.Create(options), clusterOptions);
        }

        // Azure Table Storage returns at most 1,000 entities per response page.
        protected override int ConformanceConcurrencyRowCount => 1001;

        protected override MembershipTableTestFixture CreateConformanceFixture()
        {
            TestUtils.CheckForAzureStorage();
            var options = new AzureStorageClusteringOptions();
            var probe = AzureStorageOperationOptionsExtensions.GetTableServiceClient().GetTableClient(options.TableName);
            return new MembershipTableTestFixture(
                GetType().Name,
                (serviceId, clusterId, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(new MembershipTableTestHandle(CreateMembershipTable(
                        loggerFactory.CreateLogger<AzureBasedMembershipTable>(),
                        Options.Create(new ClusterOptions { ServiceId = serviceId, ClusterId = clusterId }))));
                },
                async (clusterId, cancellationToken) =>
                {
                    // The partition contains both silo entities and the membership version row.
                    await foreach (var page in probe.QueryAsync<TableEntity>(
                        TableClient.CreateQueryFilter($"PartitionKey eq {clusterId}"),
                        maxPerPage: 1,
                        select: [nameof(TableEntity.PartitionKey), nameof(TableEntity.RowKey)],
                        cancellationToken: cancellationToken).AsPages())
                    {
                        if (page.Values.Count > 0)
                        {
                            return false;
                        }
                    }

                    return true;
                });
        }

        /// <summary>
        /// Creates an Azure-based gateway list provider for client connections.
        /// This provider queries Azure Table Storage to discover available
        /// gateway silos that clients can connect to.
        /// </summary>
        protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
        {
            var options = new AzureStorageGatewayOptions();
            options.ConfigureTestDefaults();
            return new AzureGatewayListProvider(loggerFactory, Options.Create(options), this._clusterOptions, this._gatewayOptions);
        }

        protected override Task<string> GetConnectionString()
        {
            TestUtils.CheckForAzureStorage();
            return Task.FromResult("not used");
        }

        [Fact, TestCategory("Functional")]
        public Task MembershipTable_Azure_Init()
            => InitializeLegacyMembershipTableAsync(TestContext.Current.CancellationToken);

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(1001)]
        public async Task MembershipTable_Azure_ReadAll_UpgradesLegacyBoundaryRows(int rowCount)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var client = AzureStorageOperationOptionsExtensions.GetTableServiceClient()
                .GetTableClient(new AzureStorageClusteringOptions().TableName);
            await client.CreateIfNotExistsAsync(cancellationToken);
            var version = new SiloInstanceTableEntry
            {
                PartitionKey = clusterId,
                RowKey = SiloInstanceTableEntry.TABLE_VERSION_ROW,
                DeploymentId = clusterId,
                MembershipVersion = "7"
            };
            await client.AddEntityAsync(version, cancellationToken);
            var legacyVersion = await client.GetEntityAsync<SiloInstanceTableEntry>(
                clusterId, version.RowKey, cancellationToken: cancellationToken);
            var startTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            // Seed the 10.2.x format directly: VersionRow and silo rows, with neither boundary marker.
            var rows = Enumerable.Range(0, rowCount).Select(index =>
            {
                var address = SiloAddress.New(IPAddress.Loopback, 12000 + index, 1);
                return new SiloInstanceTableEntry
                {
                    PartitionKey = clusterId,
                    RowKey = SiloInstanceTableEntry.ConstructRowKey(address),
                    DeploymentId = clusterId,
                    Address = address.Endpoint.Address.ToString(),
                    Port = address.Endpoint.Port.ToString(CultureInfo.InvariantCulture),
                    Generation = "1",
                    HostName = $"legacy-{index}",
                    Status = nameof(SiloStatus.Active),
                    StartTime = LogFormatter.PrintDate(startTime),
                    IAmAliveTime = LogFormatter.PrintDate(startTime)
                };
            }).ToArray();
            foreach (var batch in rows.Chunk(100))
            {
                await client.SubmitTransactionAsync(
                    batch.Select(row => new TableTransactionAction(TableTransactionActionType.Add, row)), cancellationToken);
            }

            var legacyEtags = new Dictionary<string, string>();
            await foreach (var row in client.QueryAsync<SiloInstanceTableEntry>(
                TableClient.CreateQueryFilter($"PartitionKey eq {clusterId}"), cancellationToken: cancellationToken))
            {
                legacyEtags.Add(row.RowKey, row.ETag.ToString());
            }

            var table = await GetLegacyMembershipTableAsync(cancellationToken);
            var result = await table.ReadAllAsync(cancellationToken);

            Assert.Equal(7, result.Version.Version);
            Assert.NotEqual(legacyVersion.Value.ETag.ToString(), result.Version.VersionEtag);
            Assert.Equal(rowCount, result.Members.Count);
            for (var index = 0; index < rowCount; index++)
            {
                var row = Assert.IsType<Tuple<MembershipEntry, string>>(result.TryGet(
                    SiloInstanceTableEntry.UnpackRowKey(rows[index].RowKey)));
                Assert.Equal($"legacy-{index}", row.Item1.HostName);
                Assert.Equal(SiloStatus.Active, row.Item1.Status);
                Assert.Equal(startTime, row.Item1.StartTime);
                Assert.Equal(startTime, row.Item1.IAmAliveTime);
                Assert.Equal(legacyEtags[rows[index].RowKey], row.Item2);
            }

            foreach (var rowKey in new[] { SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX })
            {
                var boundary = await client.GetEntityAsync<SiloInstanceTableEntry>(
                    clusterId, rowKey, cancellationToken: cancellationToken);
                Assert.Equal("7", boundary.Value.MembershipVersion);
                Assert.Null(boundary.Value.Status);
            }

            Assert.Equal(result.Version, (await table.ReadAllAsync(cancellationToken)).Version);
            var joining = new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 14000, 2),
                HostName = "new-silo",
                Status = SiloStatus.Joining,
                StartTime = startTime,
                IAmAliveTime = startTime
            };
            Assert.False(await table.InsertRowAsync(
                joining, new TableVersion(8, legacyVersion.Value.ETag.ToString()), cancellationToken));
            Assert.True(await table.InsertRowAsync(joining, result.Version.Next(), cancellationToken));
            var joined = await table.ReadAllAsync(cancellationToken);
            Assert.Equal(8, joined.Version.Version);
            Assert.Equal(rowCount + 1, joined.Members.Count);
            var joinedRow = Assert.IsType<Tuple<MembershipEntry, string>>(joined.TryGet(joining.SiloAddress));
            joining.Status = SiloStatus.Active;
            Assert.True(await table.UpdateRowAsync(joining, joinedRow.Item2, joined.Version.Next(), cancellationToken));
            var active = await table.ReadAllAsync(cancellationToken);
            Assert.Equal(9, active.Version.Version);
            Assert.Equal(SiloStatus.Active,
                Assert.IsType<Tuple<MembershipEntry, string>>(active.TryGet(joining.SiloAddress)).Item1.Status);
        }

        /// <summary>
        /// Tests inserting a new silo entry into Azure Table Storage.
        /// Verifies that the entry is correctly stored with all required
        /// properties and can handle Azure's entity size limitations.
        /// </summary>
        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        /// <summary>
        /// Tests concurrent updates to membership entries.
        /// Verifies that Azure Table Storage's optimistic concurrency control
        /// correctly handles simultaneous updates from multiple silos.
        /// </summary>
        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel();
        }

        /// <summary>
        /// Tests the heartbeat mechanism using Azure Table Storage.
        /// Verifies that silos can efficiently update their liveness
        /// timestamps without conflicts or excessive storage operations.
        /// </summary>
        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_UpdateIAmAlive()
        {
            await MembershipTable_UpdateIAmAlive();
        }
    }
}
