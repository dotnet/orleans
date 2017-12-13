using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ReminderService;
using Orleans.Tests.SqlUtils;
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
        public MySqlRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, CreateFilters())
        {
        }
        private static LoggerFilterOptions CreateFilters()
        {
            var filters = new LoggerFilterOptions();
            filters.AddFilter(nameof(MySqlRemindersTableTests), LogLevel.Trace);
            return filters;
        }

        protected override IReminderTable CreateRemindersTable()
        {
            return new SqlReminderTable(this.ClusterFixture.Services.GetRequiredService<IGrainReferenceConverter>(), this.siloOptions);
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