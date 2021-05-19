using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Clustering.AzureStorage;
using Orleans.Messaging;
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
        public AzureMembershipTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, CreateFilters())
        {
        }

        private static LoggerFilterOptions CreateFilters()
        {
            var filters = new LoggerFilterOptions();
            filters.AddFilter(typeof(Orleans.Clustering.AzureStorage.AzureTableDataManager<>).FullName, LogLevel.Trace);
            filters.AddFilter(typeof(OrleansSiloInstanceManager).FullName, LogLevel.Trace);
            filters.AddFilter("Orleans.Storage", LogLevel.Trace);
            return filters;
        }

        protected override IMembershipTable CreateMembershipTable(ILogger logger)
        {
            TestUtils.CheckForAzureStorage();
            var options = new AzureStorageClusteringOptions();
            options.ConfigureTestDefaults();
            return new AzureBasedMembershipTable(loggerFactory, Options.Create(options), this.clusterOptions);
        }

        protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
        {
            var options = new AzureStorageGatewayOptions();
            options.ConfigureTestDefaults();
            return new AzureGatewayListProvider(loggerFactory, Options.Create(options), this.clusterOptions, this.gatewayOptions);
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
