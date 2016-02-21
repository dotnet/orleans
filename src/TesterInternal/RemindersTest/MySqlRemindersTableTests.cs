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
    /// Tests for operation of Orleans Reminders Table using MySQL
    /// </summary>
    [TestClass]
    public class MySqlRemindersTableTests:ReminderTableTestsBase
    {
        private const string testDatabaseName = "OrleansTest";

        // Use ClassInitialize to run code before running the first test in the class
        [ClassInitialize]
        public new static void ClassInitialize(TestContext testContext)
        {
            ReminderTableTestsBase.ClassInitialize();
            TraceLogger.AddTraceLevelOverride(typeof(MySqlRemindersTableTests).Name, Severity.Verbose3);
        }

        protected override IReminderTable CreateRemindersTable()
        {
            return new SqlReminderTable();
        }

        protected override string GetAdoInvariant()
        {
            return AdoNetInvariants.InvariantNameMySql;
        }

        protected override string GetConnectionString()
        {
            return RelationalStorageForTesting.SetupInstance(GetAdoInvariant(), testDatabaseName)
                    .Result.CurrentConnectionString;
        }


        [TestMethod, TestCategory("Reminders"), TestCategory("MySql")]
        public void RemindersTable_MySql_Init()
        {
        }


        [TestMethod, TestCategory("Reminders"), TestCategory("MySql")]
        public async Task RemindersTable_MySql_RemindersRange()
        {
            await RemindersRange();
        }

        [TestMethod, TestCategory("Reminders"), TestCategory("MySql")]
        public async Task RemindersTable_MySql_RemindersParallelUpsert()
        {
            await RemindersParallelUpsert();
        }

        [TestMethod, TestCategory("Reminders"), TestCategory("MySql")]
        public async Task RemindersTable_MySql_ReminderSimple()
        {
            await ReminderSimple();
        }
    }
}