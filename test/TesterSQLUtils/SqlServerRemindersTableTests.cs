using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Orleans.SqlUtils;
using TestExtensions;
using UnitTests.General;
using Xunit;

namespace UnitTests.RemindersTest
{
    /// <summary>
    /// Tests for operation of Orleans Reminders Table using SQL Server
    /// </summary>
    public class SqlServerRemindersTableTests : ReminderTableTestsBase
    {
        public SqlServerRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment)
        {
            LogManager.AddTraceLevelOverride(nameof (SqlServerRemindersTableTests), Severity.Verbose3);
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

        [Fact, TestCategory("Reminders"), TestCategory("SqlServer")]
        public void RemindersTable_SqlServer_Init()
        {
        }

        [Fact, TestCategory("Reminders"), TestCategory("SqlServer")]
        public async Task RemindersTable_SqlServer_RemindersRange()
        {
            await RemindersRange();
        }


        [Fact, TestCategory("Reminders"), TestCategory("SqlServer")]
        public async Task RemindersTable_SqlServer_RemindersParallelUpsert()
        {
            await RemindersParallelUpsert();
        }

        [Fact, TestCategory("Reminders"), TestCategory("SqlServer")]
        public async Task RemindersTable_SqlServer_ReminderSimple()
        {
            await ReminderSimple();
        }
    }
}
