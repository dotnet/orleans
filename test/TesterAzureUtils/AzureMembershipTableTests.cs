using System.Threading.Tasks;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.TestingHost;
using Tester;
using TestExtensions;
using UnitTests;
using UnitTests.MembershipTests;
using UnitTests.StorageTests;
using Xunit;

namespace Tester.AzureUtils
{
    /// <summary>
    /// Tests for operation of Orleans Membership Table using AzureStore - Requires access to external Azure storage
    /// </summary>
    public class AzureMembershipTableTests : MembershipTableTestsBase, IClassFixture<AzureStorageBasicTestFixture>
    {
        public AzureMembershipTableTests(ConnectionStringFixture fixture):base(fixture)
        {
            LogManager.AddTraceLevelOverride("AzureTableDataManager", Severity.Verbose3);
            LogManager.AddTraceLevelOverride("OrleansSiloInstanceManager", Severity.Verbose3);
            LogManager.AddTraceLevelOverride("Storage", Severity.Verbose3);
        }

        protected override IMembershipTable CreateMembershipTable(Logger logger)
        {
            return new AzureBasedMembershipTable();
        }

        protected override IGatewayListProvider CreateGatewayListProvider(Logger logger)
        {
            return new AzureGatewayListProvider();
        }

        protected override string GetConnectionString()
        {
            return TestDefaultConfiguration.DataConnectionString;
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public void MembershipTable_Azure_Init()
        {
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task MembershipTable_Azure_GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task MembershipTable_Azure_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task MembershipTable_Azure_InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task MembershipTable_Azure_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task MembershipTable_Azure_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task MembershipTable_Azure_UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        [Fact, TestCategory("Membership"), TestCategory("Azure")]
        public async Task MembershipTable_Azure_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel();
        }
    }
}
