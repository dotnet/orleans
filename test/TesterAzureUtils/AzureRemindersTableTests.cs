using System.Threading.Tasks;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Tester;
using Tester.AzureUtils;
using TestExtensions;
using UnitTests.MembershipTests;
using UnitTests.StorageTests;
using Xunit;

namespace UnitTests.RemindersTest
{
    /// <summary>
    /// Tests for operation of Orleans Reminders Table using Azure
    /// </summary>
    public class AzureRemindersTableTests : ReminderTableTestsBase, IClassFixture<AzureStorageBasicTestFixture>
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
            return new AzureBasedReminderTable();
        }

        protected override string GetConnectionString()
        {
            return TestDefaultConfiguration.DataConnectionString;
        }

        [Fact, TestCategory("Reminders"), TestCategory("Azure")]
        public void RemindersTable_Azure_Init()
        {
        }

        [Fact, TestCategory("Reminders"), TestCategory("Azure")]
        public async Task RemindersTable_Azure_RemindersRange()
        {
            await RemindersRange(50);
        }

        [Fact, TestCategory("Reminders"), TestCategory("Azure")]
        public async Task RemindersTable_Azure_RemindersParallelUpsert()
        {
            await RemindersParallelUpsert();
        }

        [Fact, TestCategory("Reminders"), TestCategory("Azure")]
        public async Task RemindersTable_Azure_ReminderSimple()
        {
            await ReminderSimple();
        }
    }
}