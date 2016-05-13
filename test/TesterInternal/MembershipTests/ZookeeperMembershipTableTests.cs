using System.Threading.Tasks;
using Orleans;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.MembershipTests
{
    /// <summary>
    /// Tests for operation of Orleans SiloInstanceManager using ZookeeperStore - Requires access to external Zookeeper storage
    /// </summary>
    public class ZookeeperMembershipTableTests : MembershipTableTestsBase
    {
        public ZookeeperMembershipTableTests(ConnectionStringFixture fixture) : base(fixture)
        {
            TraceLogger.AddTraceLevelOverride(typeof (ZookeeperMembershipTableTests).Name, Severity.Verbose3);
        }

        protected override IMembershipTable CreateMembershipTable(TraceLogger logger)
        {
            return AssemblyLoader.LoadAndCreateInstance<IMembershipTable>(Constants.ORLEANS_ZOOKEEPER_UTILS_DLL, logger);
        }

        protected override IGatewayListProvider CreateGatewayListProvider(TraceLogger logger)
        {
            return AssemblyLoader.LoadAndCreateInstance<IGatewayListProvider>(Constants.ORLEANS_ZOOKEEPER_UTILS_DLL, logger);
        }

        protected override string GetConnectionString()
        {
            return StorageTestConstants.GetZooKeeperConnectionString();
        }

        [Fact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public void MembershipTable_ZooKeeper_Init()
        {
        }

        [Fact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task MembershipTable_ZooKeeper_GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        [Fact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task MembershipTable_ZooKeeper_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [Fact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task MembershipTable_ZooKeeper_InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [Fact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task MembershipTable_ZooKeeper_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [Fact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task MembershipTable_ZooKeeper_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [Fact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task MembershipTable_ZooKeeper_UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        [Fact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task MembershipTable_ZooKeeper_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel();
        }
    }
}
