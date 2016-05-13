using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Orleans.TestingHost;
using UnitTests.StorageTests;
using Xunit;

namespace UnitTests.RemindersTest
{
    /// <summary>
    /// Tests for operation of Orleans Reminders Table using Azure
    /// </summary>
    public class AzureRemindersTableTests : ReminderTableTestsBase, IClassFixture<AzureStorageBasicTestFixture>
    {
        public AzureRemindersTableTests(ConnectionStringFixture fixture) : base(fixture)
        {
            TraceLogger.AddTraceLevelOverride("AzureTableDataManager", Severity.Verbose3);
            TraceLogger.AddTraceLevelOverride("OrleansSiloInstanceManager", Severity.Verbose3);
            TraceLogger.AddTraceLevelOverride("Storage", Severity.Verbose3);
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