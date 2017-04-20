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
    /// Tests for operation of Orleans Reminders Table using SQL Server
    /// </summary>
    [TestCategory("Reminders"), TestCategory("SqlServer")]
    public class SqlServerRemindersTableTests : ReminderTableTestsBase
    {
        public SqlServerRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment)
        {
            LogManager.AddTraceLevelOverride(nameof (SqlServerRemindersTableTests), Severity.Verbose3);
        }

        protected override IReminderTable CreateRemindersTable()
        {
            return new SqlReminderTable(this.ClusterFixture.Services.GetRequiredService<IGrainReferenceConverter>());
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
            await RemindersRange();
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
