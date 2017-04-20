using System.Threading.Tasks;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using TestExtensions;
using UnitTests;
using UnitTests.MembershipTests;
using Xunit;

namespace Tester.AzureUtils
{
    /// <summary>
    /// Tests for operation of Orleans Membership Table using AzureStore - Requires access to external Azure storage
    /// </summary>
    [TestCategory("Membership"), TestCategory("Azure")]
    public class AzureMembershipTableTests : MembershipTableTestsBase, IClassFixture<AzureStorageBasicTests>
    {
        public AzureMembershipTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment)
        {
            LogManager.AddTraceLevelOverride("AzureTableDataManager", Severity.Verbose3);
            LogManager.AddTraceLevelOverride("OrleansSiloInstanceManager", Severity.Verbose3);
            LogManager.AddTraceLevelOverride("Storage", Severity.Verbose3);
        }

        protected override IMembershipTable CreateMembershipTable(Logger logger)
        {
            TestUtils.CheckForAzureStorage();
            return new AzureBasedMembershipTable();
        }

        protected override IGatewayListProvider CreateGatewayListProvider(Logger logger)
        {
            return new AzureGatewayListProvider();
        }

        protected override Task<string> GetConnectionString()
        {
            TestUtils.CheckForAzureStorage();
            return Task.FromResult(TestDefaultConfiguration.DataConnectionString);
        }

        [SkippableFact, TestCategory("Functional")]
        public void MembershipTable_Azure_Init()
        {
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Azure_UpdateIAmAlive()
        {
            await MembershipTable_UpdateIAmAlive();
        }
    }
}
