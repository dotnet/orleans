using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using UnitTests.General;
using Xunit;

namespace UnitTests.RemindersTest
{
    [TestCategory("Functional"), TestCategory("Reminders"), TestCategory("AdoNet"), TestCategory("PostgreSql")]
    public class PostgreSqlRemindersTableTests : ReminderTableTestsBase
    {
        public PostgreSqlRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, CreateFilters())
        {
        }

        private static LoggerFilterOptions CreateFilters()
        {
            var filters = new LoggerFilterOptions();
            filters.AddFilter(nameof(PostgreSqlRemindersTableTests), LogLevel.Trace);
            return filters;
        }

        protected override IReminderTable CreateRemindersTable()
        {
            var options = new AdoNetReminderTableOptions
            {
                Invariant = this.GetAdoInvariant(),
                ConnectionString = this.connectionStringFixture.ConnectionString
            };
            return new AdoNetReminderTable(
                this.clusterOptions,
                Options.Create(options));
        }

        protected override string GetAdoInvariant()
        {
            return AdoNetInvariants.InvariantNamePostgreSql;
        }

        protected override async Task<string> GetConnectionString()
        {
            var instance = await RelationalStorageForTesting.SetupInstance(GetAdoInvariant(), testDatabaseName);
            return instance.CurrentConnectionString;
        }

        [SkippableFact]
        public void RemindersTable_PostgreSql_Init()
        {
        }


        [SkippableFact]
        public async Task RemindersTable_PostgreSql_RemindersRange()
        {
            await RemindersRange(iterations: 50);
        }

        [SkippableFact]
        public async Task RemindersTable_PostgreSql_RemindersParallelUpsert()
        {
            await RemindersParallelUpsert();
        }

        [SkippableFact]
        public async Task RemindersTable_PostgreSql_ReminderSimple()
        {
            await ReminderSimple();
        }
    }
}