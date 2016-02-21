using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.TestingHost;
using Tester;


namespace UnitTests.MembershipTests
{
    /// <summary>
    /// Tests for operation of Orleans Membership Table using AzureStore - Requires access to external Azure storage
    /// </summary>
    [TestClass]
    public class AzureMembershipTableTests : MembershipTableTestsBase
    {
        [ClassInitialize]
        public new static void ClassInitialize(TestContext testContext)
        {
            MembershipTableTestsBase.ClassInitialize();
            TraceLogger.AddTraceLevelOverride("AzureTableDataManager", Severity.Verbose3);
            TraceLogger.AddTraceLevelOverride("OrleansSiloInstanceManager", Severity.Verbose3);
            TraceLogger.AddTraceLevelOverride("Storage", Severity.Verbose3);

            TestUtils.CheckForAzureStorage();
        }

        protected override IMembershipTable CreateMembershipTable(TraceLogger logger)
        {
            return new AzureBasedMembershipTable();
        }

        protected override string GetConnectionString()
        {
            return StorageTestConstants.DataConnectionString;
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public void MembershipTable_Azure_Init()
        {
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task MembershipTable_Azure_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task MembershipTable_Azure_InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task MembershipTable_Azure_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task MembershipTable_Azure_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task MembershipTable_Azure_UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        [TestMethod, TestCategory("Membership"), TestCategory("Azure")]
        public async Task MembershipTable_Azure_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel();
        }
    }
}
