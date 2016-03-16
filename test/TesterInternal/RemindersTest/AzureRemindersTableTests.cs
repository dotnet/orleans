using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Orleans.TestingHost;
using Tester;
using Xunit;

namespace UnitTests.RemindersTest
{
    /// <summary>
    /// Tests for operation of Orleans Reminders Table using Azure
    /// </summary>
    public class AzureRemindersTableTests : ReminderTableTestsBase
    {
        public AzureRemindersTableTests()
        {
            TraceLogger.AddTraceLevelOverride("AzureTableDataManager", Severity.Verbose3);
            TraceLogger.AddTraceLevelOverride("OrleansSiloInstanceManager", Severity.Verbose3);
            TraceLogger.AddTraceLevelOverride("Storage", Severity.Verbose3);

            TestUtils.CheckForAzureStorage();
        }

        protected override IReminderTable CreateRemindersTable()
        {
            return new AzureBasedReminderTable();
        }

        protected override string GetConnectionString()
        {
            return StorageTestConstants.DataConnectionString;
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