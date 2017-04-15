using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Tester;
using Tester.AzureUtils;
using TestExtensions;
using Xunit;

namespace UnitTests.RemindersTest
{
    /// <summary>
    /// Tests for operation of Orleans Reminders Table using Azure
    /// </summary>
    [TestCategory("Reminders"), TestCategory("Azure")]
    public class AzureRemindersTableTests : ReminderTableTestsBase, IClassFixture<AzureStorageBasicTests>
    {
        public AzureRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment)
        {
            LogManager.AddTraceLevelOverride("AzureTableDataManager", Severity.Verbose3);
            LogManager.AddTraceLevelOverride("OrleansSiloInstanceManager", Severity.Verbose3);
            LogManager.AddTraceLevelOverride("Storage", Severity.Verbose3);
        }

        public override void Dispose()
        {
            // Reset init timeout after tests
            OrleansSiloInstanceManager.initTimeout = AzureTableDefaultPolicies.TableCreationTimeout;
            base.Dispose();
        }

        protected override IReminderTable CreateRemindersTable()
        {
            TestUtils.CheckForAzureStorage();
            return new AzureBasedReminderTable(this.ClusterFixture.Services.GetRequiredService<IGrainReferenceConverter>());
        }

        protected override Task<string> GetConnectionString()
        {
            TestUtils.CheckForAzureStorage();
            return Task.FromResult(TestDefaultConfiguration.DataConnectionString);
        }

        [SkippableFact]
        public void RemindersTable_Azure_Init()
        {
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task RemindersTable_Azure_RemindersRange()
        {
            await RemindersRange(50);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task RemindersTable_Azure_RemindersParallelUpsert()
        {
            await RemindersParallelUpsert();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task RemindersTable_Azure_ReminderSimple()
        {
            await ReminderSimple();
        }
    }
}