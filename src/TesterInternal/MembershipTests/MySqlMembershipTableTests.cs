using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.AzureUtils;
using Orleans.SqlUtils;
using UnitTests.StorageTests;
using UnitTests.General;

namespace UnitTests.MembershipTests
{
    /// <summary>
    /// Tests for operation of Orleans Membership Table using MySQL
    /// </summary>
    [TestClass]
    public class MySqlMembershipTableTests
    {
        public TestContext TestContext { get; set; }

        private string deploymentId;
        private SiloAddress siloAddress;
        private IMembershipTable membership;
        private static string connectionString;
        private const string testDatabaseName = "OrleansTest";
        private static readonly TimeSpan timeout = TimeSpan.FromMinutes(1);
        private readonly TraceLogger logger = TraceLogger.GetLogger("MySqlMembershipTableTests", TraceLogger.LoggerType.Application);

        // Use ClassInitialize to run code before running the first test in the class
        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext)
        {
            TraceLogger.Initialize(new NodeConfiguration());
            TraceLogger.AddTraceLevelOverride("MySqlMembershipTableTests", Severity.Verbose3);

            // Set shorter init timeout for these tests
            OrleansSiloInstanceManager.initTimeout = TimeSpan.FromSeconds(20);

            connectionString = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameMySql, testDatabaseName).Result.CurrentConnectionString;
        }


        private async Task Initialize()
        {
            deploymentId = "test-" + Guid.NewGuid();
            int generation = SiloAddress.AllocateNewGeneration();
            siloAddress = SiloAddress.NewLocalAddress(generation);

            logger.Info("DeploymentId={0} Generation={1}", deploymentId, generation);

            GlobalConfiguration config = new GlobalConfiguration
            {
                DeploymentId = deploymentId,
                AdoInvariant = AdoNetInvariants.InvariantNameMySql,
                DataConnectionString = connectionString
            };

            var mbr = new SqlMembershipTable();
            await mbr.InitializeMembershipTable(config, true, logger).WithTimeout(timeout);
            membership = mbr;
        }


        // Use TestCleanup to run code after each test has run
        [TestCleanup]
        public void TestCleanup()
        {
            if (membership != null && SiloInstanceTableTestConstants.DeleteEntriesAfterTest)
            {
                membership.DeleteMembershipTableEntries(deploymentId).Wait();
                membership = null;
            }
            logger.Info("Test {0} completed - Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
        }


        [ClassCleanup]
        public static void ClassCleanup()
        {
            // Reset init timeout after tests
            OrleansSiloInstanceManager.initTimeout = AzureTableDefaultPolicies.TableCreationTimeout;
        }


        [TestMethod, TestCategory("Membership"), TestCategory("MySql")]
        public async Task MembershipTable_MySql_Init()
        {
            await Initialize();
            Assert.IsNotNull(membership, "Membership Table handler created");
        }


        [TestMethod, TestCategory("Membership"), TestCategory("MySql")]
        public async Task MembershipTable_MySql_ReadAll_EmptyTable()
        {
            await Initialize();
            await MembershipTablePluginTests.MembershipTable_ReadAll_EmptyTable(membership);
        }


        [TestMethod, TestCategory("Membership"), TestCategory("MySql")]
        public async Task MembershipTable_MySql_InsertRow()
        {
            await Initialize();
            await MembershipTablePluginTests.MembershipTable_InsertRow(membership);
        }


        [TestMethod, TestCategory("Membership"), TestCategory("MySql")]
        public async Task MembershipTable_MySql_ReadRow_Insert_Read()
        {
            await Initialize();
            await MembershipTablePluginTests.MembershipTable_ReadRow_Insert_Read(membership);
        }


        [TestMethod, TestCategory("Membership"), TestCategory("MySql")]
        public async Task MembershipTable_MySql_ReadAll_Insert_ReadAll()
        {
            await Initialize();
            await MembershipTablePluginTests.MembershipTable_ReadAll_Insert_ReadAll(membership);
        }

        [TestMethod, TestCategory("Membership"), TestCategory("MySql")]
        public async Task MembershipTable_MySql_UpdateRow()
        {
            await Initialize();
            await MembershipTablePluginTests.MembershipTable_UpdateRow(membership);
        }
    }
}
