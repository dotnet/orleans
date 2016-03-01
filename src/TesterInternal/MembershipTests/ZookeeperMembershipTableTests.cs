using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;


namespace UnitTests.MembershipTests
{
    /// <summary>
    /// Tests for operation of Orleans SiloInstanceManager using ZookeeperStore - Requires access to external Zookeeper storage
    /// </summary>
    [TestClass]
    [DeploymentItem("OrleansZooKeeperUtils.dll")]
    public class ZookeeperMembershipTableTests : MembershipTableTestsBase
    {
        [ClassInitialize]
        public new static void ClassInitialize(TestContext testContext)
        {
            MembershipTableTestsBase.ClassInitialize();
            TraceLogger.AddTraceLevelOverride(typeof (ZookeeperMembershipTableTests).Name, Severity.Verbose3);
        }

        protected override IMembershipTable CreateMembershipTable(TraceLogger logger)
        {
            return AssemblyLoader.LoadAndCreateInstance<IMembershipTable>(Constants.ORLEANS_ZOOKEEPER_UTILS_DLL, logger);
        }

        protected override string GetConnectionString()
        {
            return StorageTestConstants.GetZooKeeperConnectionString();
        }

        [TestMethod, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public void MembershipTable_ZooKeeper_Init()
        {
        }

        [TestMethod, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task MembershipTable_ZooKeeper_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [TestMethod, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task MembershipTable_ZooKeeper_InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [TestMethod, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task MembershipTable_ZooKeeper_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [TestMethod, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task MembershipTable_ZooKeeper_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [TestMethod, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task MembershipTable_ZooKeeper_UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        [TestMethod, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task MembershipTable_ZooKeeper_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel();
        }
    }
}
