using UnitTests.General;
using UnitTests.StorageTests;

namespace UnitTests.RemindersTest
{

using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ReminderService;
using Orleans.SqlUtils;

namespace UnitTests.RemindersTest
{
    /// <summary>
    /// Tests for operation of Orleans Reminders Table using MySQL
    /// </summary>
    [TestClass]    
    public class MySqlRemindersTableTests
    {
        public TestContext TestContext { get; set; }

        private string deploymentId;
        private static string connectionString;
        private const string testDatabaseName = "OrleansTest";
        private static readonly TimeSpan timeout = TimeSpan.FromMinutes(1);

        private readonly TraceLogger logger = TraceLogger.GetLogger("MySqlRemindersTableTests",
            TraceLogger.LoggerType.Application);

        private SqlReminderTable reminder;

        // Use ClassInitialize to run code before running the first test in the class
        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext)
        {
            TraceLogger.Initialize(new NodeConfiguration());
            TraceLogger.AddTraceLevelOverride("MySqlRemindersTableTests", Severity.Verbose3);

            // Set shorter init timeout for these tests
            OrleansSiloInstanceManager.initTimeout = TimeSpan.FromSeconds(20);
            connectionString = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameMySql, testDatabaseName).Result.CurrentConnectionString;
        }


        private async Task Initialize()
        {
            deploymentId = "test-" + Guid.NewGuid();
            int generation = SiloAddress.AllocateNewGeneration();

            logger.Info("DeploymentId={0} Generation={1}", deploymentId, generation);

            GlobalConfiguration config = new GlobalConfiguration
                                         {
                                             DeploymentId = deploymentId,
                                             DataConnectionStringForReminders = connectionString,
                                             AdoInvariantForReminders = AdoNetInvariants.InvariantNameMySql
                                         };

            var rmndr = new SqlReminderTable();
            await rmndr.Init(config, logger).WithTimeout(timeout);
            reminder = rmndr;
        }


        // Use TestCleanup to run code after each test has run
        [TestCleanup]
        public void TestCleanup()
        {
            if (reminder != null && SiloInstanceTableTestConstants.DeleteEntriesAfterTest)
            {
                reminder.TestOnlyClearTable().Wait();
                reminder = null;
            }
            logger.Info("Test {0} completed - Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
        }


        [ClassCleanup]
        public static void ClassCleanup()
        {
            // Reset init timeout after tests
            OrleansSiloInstanceManager.initTimeout = AzureTableDefaultPolicies.TableCreationTimeout;
        }


        [TestMethod, TestCategory("Reminders"), TestCategory("MySql")]
        public async Task RemindersTable_MySql_Init()
        {
            await Initialize();
            Assert.IsNotNull(reminder, "Reminder Table handler created");
        }


        [TestMethod, TestCategory("Reminders"), TestCategory("MySql")]
        public async Task RemindersTable_MySql_UpsertReminderParallel()
        {
            await Initialize();
            await ReminderTablePluginTests.ReminderTableUpsertParallel(reminder);
        }
    }
}
}
