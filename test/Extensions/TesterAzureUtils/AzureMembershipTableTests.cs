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

        [SkippableFact, TestCategory("Functional")]
        public void MembershipTable_Azure_Init()
        {
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        /// <summary>
        /// Tests inserting a new silo entry into Azure Table Storage.
        /// Verifies that the entry is correctly stored with all required
        /// properties and can handle Azure's entity size limitations.
        /// </summary>
        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        /// <summary>
        /// Tests concurrent updates to membership entries.
        /// Verifies that Azure Table Storage's optimistic concurrency control
        /// correctly handles simultaneous updates from multiple silos.
        /// </summary>
        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel();
        }

        /// <summary>
        /// Tests the heartbeat mechanism using Azure Table Storage.
        /// Verifies that silos can efficiently update their liveness
        /// timestamps without conflicts or excessive storage operations.
        /// </summary>
        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_UpdateIAmAlive()
        {
            await MembershipTable_UpdateIAmAlive();
        }
    }
}
