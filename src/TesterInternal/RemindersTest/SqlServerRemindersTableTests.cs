using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Orleans.SqlUtils;
using UnitTests.General;

namespace UnitTests.RemindersTest
{
    /// <summary>
    /// Tests for operation of Orleans Reminders Table using SQL Server
    /// </summary>
    [TestClass]
    public class SqlServerRemindersTableTests : ReminderTableTestsBase
    {
        private const string testDatabaseName = "OrleansTest";

        // Use ClassInitialize to run code before running the first test in the class
        [ClassInitialize]
        public new static void ClassInitialize(TestContext testContext)
        {
            ReminderTableTestsBase.ClassInitialize();
            TraceLogger.AddTraceLevelOverride(typeof (SqlServerRemindersTableTests).Name, Severity.Verbose3);
        }

        protected override IReminderTable CreateRemindersTable()
        {
            return new SqlReminderTable();
        }

        protected override string GetAdoInvariant()
        {
            return AdoNetInvariants.InvariantNameSqlServer;
        }

        protected override string GetConnectionString()
        {
            return RelationalStorageForTesting.SetupInstance(GetAdoInvariant(), testDatabaseName)
                .Result.CurrentConnectionString;
        }

        [TestMethod, TestCategory("Reminders"), TestCategory("SqlServer")]
        public void RemindersTable_SqlServer_Init()
        {
        }

        [TestMethod, TestCategory("Reminders"), TestCategory("SqlServer")]
        public async Task RemindersTable_SqlServer_RemindersRange()
        {
            await RemindersRange();
        }


        [TestMethod, TestCategory("Reminders"), TestCategory("SqlServer")]
        public async Task RemindersTable_SqlServer_RemindersParallelUpsert()
        {
            await RemindersParallelUpsert();
        }

        [TestMethod, TestCategory("Reminders"), TestCategory("SqlServer")]
        public async Task RemindersTable_SqlServer_ReminderSimple()
        {
            await ReminderSimple();
        }
    }
}
