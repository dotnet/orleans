using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging;
using Orleans.Runtime.Membership;
using Orleans.Configuration;
using TestExtensions;
using Xunit;
using Tester.ZooKeeperUtils;

namespace UnitTests.MembershipTests
{
    /// <summary>
    /// Tests for operation of Orleans SiloInstanceManager using ZookeeperStore - Requires access to external Zookeeper storage
    /// 
    /// Apache ZooKeeper provides a hierarchical namespace for distributed coordination.
    /// Orleans uses ZooKeeper for:
    /// - Distributed membership management using znodes (ZooKeeper nodes)
    /// - Leader election and distributed consensus
    /// - Ephemeral nodes for automatic cleanup on silo failure
    /// - Watch notifications for membership changes
    /// 
    /// These tests verify the ZooKeeper-based membership provider handles all
    /// membership operations correctly, including node failures and network partitions.
    /// </summary>
    [TestCategory("Membership"), TestCategory("ZooKeeper")]
    public class ZookeeperMembershipTableTests : MembershipTableTestsBase
    {
        public ZookeeperMembershipTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment)
            : base(fixture, environment, CreateFilters())
        {
        }

        private static LoggerFilterOptions CreateFilters()
        {
            var filters = new LoggerFilterOptions();
            filters.AddFilter(typeof(ZookeeperMembershipTableTests).Name, LogLevel.Trace);
            return filters;
        }

        /// <summary>
        /// Creates a ZooKeeper-based membership table for testing.
        /// Configures the ZooKeeper connection and creates the membership
        /// table that uses ZooKeeper's hierarchical namespace for storage.
        /// </summary>
        protected override IMembershipTable CreateMembershipTable(ILogger logger)
        {
            var options = new ZooKeeperClusteringSiloOptions();
            options.ConnectionString = this.connectionString;
           
            return new ZooKeeperBasedMembershipTable(this.Services.GetService<ILogger<ZooKeeperBasedMembershipTable>>(), Options.Create(options), this._clusterOptions);
        }

        /// <summary>
        /// Creates a ZooKeeper-based gateway list provider.
        /// This provider uses ZooKeeper watches to maintain an up-to-date
        /// list of available gateways for client connections.
        /// </summary>
        protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
        {
            var options = new ZooKeeperGatewayListProviderOptions();
            options.ConnectionString = this.connectionString;

            return ActivatorUtilities.CreateInstance<ZooKeeperGatewayListProvider>(this.Services, Options.Create(options), this._clusterOptions);
        }

        protected override async Task<string> GetConnectionString()
        {
            bool isReachable = await ZookeeperTestUtils.EnsureZooKeeperAsync();
            return isReachable ? TestDefaultConfiguration.ZooKeeperConnectionString : null;
        }

        [SkippableFact]
        public void MembershipTable_ZooKeeper_Init()
        {
        }

        [SkippableFact]
        public async Task MembershipTable_ZooKeeper_GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        [SkippableFact]
        public async Task MembershipTable_ZooKeeper_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        /// <summary>
        /// Tests inserting a silo entry as a ZooKeeper znode.
        /// Verifies that the membership data is correctly serialized
        /// and stored in ZooKeeper's hierarchical structure.
        /// </summary>
        [SkippableFact]
        public async Task MembershipTable_ZooKeeper_InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [SkippableFact]
        public async Task MembershipTable_ZooKeeper_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [SkippableFact]
        public async Task MembershipTable_ZooKeeper_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [SkippableFact]
        public async Task MembershipTable_ZooKeeper_UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        /// <summary>
        /// Tests concurrent updates using ZooKeeper's versioning.
        /// Verifies that ZooKeeper's optimistic concurrency control
        /// correctly handles simultaneous updates from multiple silos.
        /// </summary>
        [SkippableFact]
        public async Task MembershipTable_ZooKeeper_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel();
        }

        /// <summary>
        /// Tests heartbeat updates using ZooKeeper.
        /// Verifies that ephemeral nodes and session timeouts
        /// work correctly for detecting failed silos.
        /// </summary>
        [SkippableFact]
        public async Task MembershipTable_ZooKeeper_UpdateIAmAlive()
        {
            await MembershipTable_UpdateIAmAlive();
        }
    }
}
