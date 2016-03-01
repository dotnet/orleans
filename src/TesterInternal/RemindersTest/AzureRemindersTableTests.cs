using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Orleans.TestingHost;
using Tester;
using UnitTests.MembershipTests;

namespace UnitTests.RemindersTest
{
    /// <summary>
    /// Tests for operation of Orleans Reminders Table using Azure
    /// </summary>
    [TestClass]
    public class AzureRemindersTableTests : ReminderTableTestsBase
    {
        // Use ClassInitialize to run code before running the first test in the class
        [ClassInitialize]
        public new static void ClassInitialize(TestContext testContext)
        {
            MembershipTableTestsBase.ClassInitialize();
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

        [TestMethod, TestCategory("Reminders"), TestCategory("Azure")]
        public void RemindersTable_Azure_Init()
        {
        }

        [TestMethod, TestCategory("Reminders"), TestCategory("Azure")]
        public async Task RemindersTable_Azure_RemindersRange()
        {
            await RemindersRange(50);
        }

        [TestMethod, TestCategory("Reminders"), TestCategory("Azure")]
        public async Task RemindersTable_Azure_RemindersParallelUpsert()
        {
            await RemindersParallelUpsert();
        }

        [TestMethod, TestCategory("Reminders"), TestCategory("Azure")]
        public async Task RemindersTable_Azure_ReminderSimple()
        {
            await ReminderSimple();
        }
    }
}