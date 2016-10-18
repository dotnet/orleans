using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Orleans.SqlUtils;
using UnitTests.General;
using Xunit;

namespace UnitTests.RemindersTest
{
    public class PostgreSqlRemindersTableTests : ReminderTableTestsBase
    {
        public PostgreSqlRemindersTableTests(ConnectionStringFixture fixture) : base(fixture)
        {
            LogManager.AddTraceLevelOverride(nameof(PostgreSqlRemindersTableTests), Severity.Verbose3);
        }

        protected override IReminderTable CreateRemindersTable()
        {
            return new SqlReminderTable();
        }

        protected override string GetAdoInvariant()
        {
            return AdoNetInvariants.InvariantNamePostgreSql;
        }

        protected override string GetConnectionString()
        {
            return RelationalStorageForTesting.SetupInstance(GetAdoInvariant(), testDatabaseName)
                    .Result.CurrentConnectionString;
        }


        [Fact, TestCategory("Reminders"), TestCategory("PostgreSql")]
        public void RemindersTable_PostgreSql_Init()
        {
        }


        [Fact, TestCategory("Reminders"), TestCategory("PostgreSql")]
        public async Task RemindersTable_PostgreSql_RemindersRange()
        {
            await RemindersRange();
        }

        [Fact, TestCategory("Reminders"), TestCategory("PostgreSql")]
        public async Task RemindersTable_PostgreSql_RemindersParallelUpsert()
        {
            await RemindersParallelUpsert();
        }

        [Fact, TestCategory("Reminders"), TestCategory("PostgreSql")]
        public async Task RemindersTable_PostgreSql_ReminderSimple()
        {
            await ReminderSimple();
        }
    }
}