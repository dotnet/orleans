using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using UnitTests.General;
using Xunit;

namespace UnitTests.RemindersTest
{
    /// <summary>
    /// Tests for operation of Orleans Reminders Table using SQL Server
    /// </summary>
    [TestCategory("Reminders"), TestCategory("AdoNet")]
    public class SqlServerRemindersTableTests : ReminderTableTestsBase
    {
        public SqlServerRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, CreateFilters())
        {
        }

        private static LoggerFilterOptions CreateFilters()
        {
            var filters = new LoggerFilterOptions();
            filters.AddFilter(nameof(SqlServerRemindersTableTests), LogLevel.Trace);
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
                this.ClusterFixture.Services.GetRequiredService<IGrainReferenceConverter>(),
                this.siloOptions,
                Options.Create(options));
        }

        protected override string GetAdoInvariant()
        {
            return AdoNetInvariants.InvariantNameSqlServer;
        }

        protected override async Task<string> GetConnectionString()
        {
            var instance = await RelationalStorageForTesting.SetupInstance(GetAdoInvariant(), testDatabaseName);
            return instance.CurrentConnectionString;
        }

        [SkippableFact]
        public void RemindersTable_SqlServer_Init()
        {
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task RemindersTable_SqlServer_RemindersRange()
        {
            await RemindersRange(iterations: 30);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task RemindersTable_SqlServer_RemindersParallelUpsert()
        {
            await RemindersParallelUpsert();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task RemindersTable_SqlServer_ReminderSimple()
        {
            await ReminderSimple();
        }
    }
}
