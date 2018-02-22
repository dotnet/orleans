using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Clustering.DynamoDB;
using Orleans.Configuration;
using Orleans.Messaging;
using System.Threading.Tasks;
using TestExtensions;
using UnitTests;
using UnitTests.MembershipTests;
using Xunit;

namespace AWSUtils.Tests.MembershipTests
{
    /// <summary>
    /// Tests for operation of Orleans Membership Table using AWS DynamoDB - Requires access to external DynamoDB storage
    /// </summary>
    [TestCategory("Membership"), TestCategory("AWS"), TestCategory("DynamoDb")] 
    public class DynamoDBMembershipTableTest : MembershipTableTestsBase, IClassFixture<DynamoDBStorageTestsFixture>
    {
        public DynamoDBMembershipTableTest(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, CreateFilters())
        {
        }

        private static LoggerFilterOptions CreateFilters()
        {
            var filters = new LoggerFilterOptions();
            filters.AddFilter("DynamoDBDataManager", LogLevel.Trace);
            filters.AddFilter("OrleansSiloInstanceManager", LogLevel.Trace);
            filters.AddFilter("Storage", LogLevel.Trace);
            return filters;
        }

        protected override IMembershipTable CreateMembershipTable(ILogger logger)
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
                throw new SkipException("Unable to connect to AWS DynamoDB simulator");
            var options = new DynamoDBClusteringOptions();
            LegacyDynamoDBMembershipConfigurator.ParseDataConnectionString(this.connectionString, options);
            return new DynamoDBMembershipTable(this.loggerFactory, Options.Create(options), this.clusterOptions);
        }

        protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
        {
            var options = new DynamoDBGatewayOptions();
            LegacyDynamoDBGatewayListProviderConfigurator.ParseDataConnectionString(this.connectionString, options);
            return new DynamoDBGatewayListProvider(this.loggerFactory, Options.Create(options), this.clusterOptions, this.gatewayOptions);
        }

        protected override Task<string> GetConnectionString()
        {
            return Task.FromResult(AWSTestConstants.IsDynamoDbAvailable ? "Service=http://localhost:8000;" : null);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_InsertRow()
        {
            await MembershipTable_InsertRow(false);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read(false);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll(false);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_UpdateRow()
        {
            await MembershipTable_UpdateRow(false);
        }

        [SkippableFact]
        public async Task MembershipTable_DynamoDB_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel(false);
        }

        [SkippableFact]
        public async Task MembershipTable_DynamoDB_UpdateIAmAlive()
        {
            await MembershipTable_UpdateIAmAlive(false);
        }
    }
}
