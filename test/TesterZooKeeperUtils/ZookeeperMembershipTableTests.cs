using System.Threading.Tasks;
using Orleans;
using Orleans.Messaging;
using Orleans.Runtime;
using TestExtensions;
using Xunit;
using Tester.ZooKeeperUtils;

namespace UnitTests.MembershipTests
{
    /// <summary>
    /// Tests for operation of Orleans SiloInstanceManager using ZookeeperStore - Requires access to external Zookeeper storage
    /// </summary>
    [TestCategory("Membership"), TestCategory("ZooKeeper")]
    public class ZookeeperMembershipTableTests : MembershipTableTestsBase
    {
        public ZookeeperMembershipTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment)
            : base(fixture, environment)
        {
            LogManager.AddTraceLevelOverride(typeof(ZookeeperMembershipTableTests).Name, Severity.Verbose3);
        }

        protected override IMembershipTable CreateMembershipTable(Logger logger)
        {
            return AssemblyLoader.LoadAndCreateInstance<IMembershipTable>(Constants.ORLEANS_ZOOKEEPER_UTILS_DLL, logger,
                this.Services);
        }

        protected override IGatewayListProvider CreateGatewayListProvider(Logger logger)
        {
            return AssemblyLoader.LoadAndCreateInstance<IGatewayListProvider>(Constants.ORLEANS_ZOOKEEPER_UTILS_DLL,
                logger, this.Services);
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

        [SkippableFact]
        public async Task MembershipTable_ZooKeeper_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel();
        }

        [SkippableFact]
        public async Task MembershipTable_ZooKeeper_UpdateIAmAlive()
        {
            await MembershipTable_UpdateIAmAlive();
        }
    }
}
