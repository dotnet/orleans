using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
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
    /// Tests for operation of Orleans Reminders Table using MySQL
    /// </summary>
    [TestCategory("Reminders"), TestCategory("MySql")]
    public class MySqlRemindersTableTests : ReminderTableTestsBase
    {
        public MySqlRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment)
        {
            LogManager.AddTraceLevelOverride(nameof(MySqlRemindersTableTests), Severity.Verbose3);
        }

        protected override IReminderTable CreateRemindersTable()
        {
            return new SqlReminderTable(this.ClusterFixture.Services.GetRequiredService<IGrainReferenceConverter>());
        }

        protected override string GetAdoInvariant()
        {
            return AdoNetInvariants.InvariantNameMySql;
        }

        protected override async Task<string> GetConnectionString()
        {
            var instance = await RelationalStorageForTesting.SetupInstance(GetAdoInvariant(), testDatabaseName);
            return instance.CurrentConnectionString;
        }

        [SkippableFact]
        public void RemindersTable_MySql_Init()
        {
        }


        [SkippableFact]
        public async Task RemindersTable_MySql_RemindersRange()
        {
            await RemindersRange();
        }

        [SkippableFact]
        public async Task RemindersTable_MySql_RemindersParallelUpsert()
        {
            await RemindersParallelUpsert();
        }

        [SkippableFact]
        public async Task RemindersTable_MySql_ReminderSimple()
        {
            await ReminderSimple();
        }
    }
}