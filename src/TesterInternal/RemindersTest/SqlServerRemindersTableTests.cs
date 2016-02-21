using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ReminderService;
using Orleans.SqlUtils;
using UnitTests.StorageTests;
using UnitTests.General;
using System.Xml;

namespace UnitTests.RemindersTest
{
    /// <summary>
    /// Tests for operation of Orleans Reminders Table using SQL Server
    /// </summary>
    [TestClass]    
    public class SqlServerRemindersTableTests
    {
        public TestContext TestContext { get; set; }

        private string deploymentId;
        private static string connectionString;
        private const string testDatabaseName = "OrleansTest";
        private static readonly TimeSpan timeout = TimeSpan.FromMinutes(1);

        private readonly TraceLogger logger = TraceLogger.GetLogger("SqlServerRemindersTableTests",
            TraceLogger.LoggerType.Application);

        private SqlReminderTable reminder;

        // Use ClassInitialize to run code before running the first test in the class
        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext)
        {
            TraceLogger.Initialize(new NodeConfiguration());
            TraceLogger.AddTraceLevelOverride("SqlServerRemindersTableTests", Severity.Verbose3);

            // Set shorter init timeout for these tests
            OrleansSiloInstanceManager.initTimeout = TimeSpan.FromSeconds(20);
            connectionString = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameSqlServer, testDatabaseName).Result.CurrentConnectionString;
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
                                             AdoInvariantForReminders = AdoNetInvariants.InvariantNameSqlServer
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


        [TestMethod, TestCategory("Reminders"), TestCategory("SqlServer")]
        public async Task RemindersTable_SqlServer_Init()
        {
            await Initialize();
            Assert.IsNotNull(reminder, "Reminder Table handler created");
        }


        [TestMethod, TestCategory("Reminders"), TestCategory("SqlServer")]
        public async Task RemindersTable_SqlServer_RemindersTest()
        {
            await Initialize();
            await ReminderTablePluginTests.ReminderTableTest(reminder);
        }
    }
}
