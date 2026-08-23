using System.Collections.Immutable;
using System.Net;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.AzureUtils;
using Orleans.Clustering.AzureStorage;
using Orleans.Messaging;
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
        {
            TestUtils.CheckForAzureStorage();
            var options = new AzureStorageClusteringOptions();
            options.ConfigureTestDefaults();
            return new AzureBasedMembershipTable(loggerFactory, Options.Create(options), this._clusterOptions);
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
        public void MembershipTable_Azure_Init()
        {
        }

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
        public async Task MembershipTable_Azure_MetadataRoundTrips()
        {
            await MembershipTable_MetadataRoundTrips();
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

        [Fact, TestCategory("Functional")]
        public async Task MembershipMetadata_SurvivesLegacyReplacement_RejectsConflict_AndIsCleanedUp()
        {
            var options = new AzureStorageClusteringOptions();
            options.ConfigureTestDefaults();
            var membership = new AzureBasedMembershipTable(loggerFactory, Options.Create(options), _clusterOptions);
            await membership.InitializeMembershipTable(false);

            var entry = CreateMetadataEntry();
            var initial = await membership.ReadAll();
            Assert.True(await membership.InsertRow(entry, initial.Version.Next()));

            var table = options.TableServiceClient!.GetTableClient(options.TableName);
            var rowKey = SiloInstanceTableEntry.ConstructRowKey(entry.SiloAddress);
            var legacyEntity = (await table.GetEntityAsync<TableEntity>(clusterId, rowKey)).Value;
            legacyEntity.Remove(nameof(SiloInstanceTableEntry.Metadata));
            await table.UpdateEntityAsync(legacyEntity, ETag.All, TableUpdateMode.Replace);

            var afterLegacyWrite = await membership.ReadRow(entry.SiloAddress);
            var stored = Assert.Single(afterLegacyWrite.Members).Item1;
            Assert.Equal(entry.Metadata, stored.Metadata);

            stored.Metadata = ImmutableDictionary<string, string>.Empty.Add("region", "conflict");
            stored.Status = SiloStatus.Dead;
            Assert.True(await membership.UpdateRow(
                stored,
                Assert.Single(afterLegacyWrite.Members).Item2,
                afterLegacyWrite.Version.Next()));

            var afterConflict = await membership.ReadRow(entry.SiloAddress);
            Assert.Equal(entry.Metadata, Assert.Single(afterConflict.Members).Item1.Metadata);

            await membership.CleanupDefunctSiloEntries(DateTimeOffset.UtcNow.AddDays(1));
            Assert.Empty((await membership.ReadAll()).Members);

            var metadataTable = options.TableServiceClient.GetTableClient(
                OrleansSiloInstanceManager.GetMetadataTableName(options.TableName));
            var exception = await Assert.ThrowsAsync<RequestFailedException>(
                () => metadataTable.GetEntityAsync<MembershipMetadataTableEntry>(clusterId, rowKey));
            Assert.Equal(404, exception.Status);
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipMetadata_LegacyCleanupOrphanIsReconciled()
        {
            var options = new AzureStorageClusteringOptions();
            options.ConfigureTestDefaults();
            var membership = new AzureBasedMembershipTable(loggerFactory, Options.Create(options), _clusterOptions);
            await membership.InitializeMembershipTable(false);
            var entry = CreateMetadataEntry();
            var initial = await membership.ReadAll();
            Assert.True(await membership.InsertRow(entry, initial.Version.Next()));

            var rowKey = SiloInstanceTableEntry.ConstructRowKey(entry.SiloAddress);
            var membershipTable = options.TableServiceClient!.GetTableClient(options.TableName);
            var metadataTable = options.TableServiceClient.GetTableClient(
                OrleansSiloInstanceManager.GetMetadataTableName(options.TableName));
            await membershipTable.DeleteEntityAsync(clusterId, rowKey, ETag.All);
            var metadataEntity = (await metadataTable.GetEntityAsync<MembershipMetadataTableEntry>(clusterId, rowKey)).Value;
            metadataEntity.CreatedAt = DateTimeOffset.UtcNow - OrleansSiloInstanceManager.MetadataOrphanGracePeriod - TimeSpan.FromMinutes(1);
            await metadataTable.UpdateEntityAsync(metadataEntity, ETag.All, TableUpdateMode.Replace);

            await membership.CleanupDefunctSiloEntries(DateTimeOffset.UtcNow);

            var exception = await Assert.ThrowsAsync<RequestFailedException>(
                () => metadataTable.GetEntityAsync<MembershipMetadataTableEntry>(clusterId, rowKey));
            Assert.Equal(404, exception.Status);
        }

        private static MembershipEntry CreateMetadataEntry() => new()
        {
            SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12345), 123456),
            HostName = "host",
            SiloName = "silo",
            Status = SiloStatus.Joining,
            StartTime = DateTime.UtcNow,
            IAmAliveTime = DateTime.UtcNow,
            Metadata = ImmutableDictionary<string, string>.Empty.Add("region", "west")
        };
    }

    [TestSuite("BVT")]
    [TestProvider("AzureStorage")]
    [TestArea("Membership")]
    public class AzureMembershipMetadataContractTests
    {
        [Fact]
        public void CompanionTableName_IsValidAndDeterministicAtMaximumLength()
        {
            var membershipTableName = "A" + new string('b', 62);

            var first = OrleansSiloInstanceManager.GetMetadataTableName(membershipTableName);
            var second = OrleansSiloInstanceManager.GetMetadataTableName(membershipTableName);

            Assert.Equal(first, second);
            Assert.Equal(63, first.Length);
            Assert.Matches("^[A-Za-z][A-Za-z0-9]{2,62}$", first);
            Assert.NotEqual(membershipTableName, first);
        }
    }
}
