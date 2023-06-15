using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Clustering.Redis;
using Orleans.Messaging;
using Xunit;
using UnitTests.MembershipTests;
using TestExtensions;
using UnitTests;
using StackExchange.Redis;

namespace Tester.Redis.Clustering
{
    // <summary>
    // Tests for operation of Orleans Membership Table using Redis
    // </summary>
    [TestCategory("Redis"), TestCategory("Clustering"), TestCategory("Functional")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class RedisMembershipTableTests : MembershipTableTestsBase
    {
        public RedisMembershipTableTests(ConnectionStringFixture fixture, CommonFixture environment) : base(fixture, environment, CreateFilters())
        {
        }

        private static LoggerFilterOptions CreateFilters()
        {
            var filters = new LoggerFilterOptions();
            return filters;
        }

        internal RedisMembershipTable membershipTable;

        protected override IMembershipTable CreateMembershipTable(ILogger logger)
        {
            TestUtils.CheckForRedis();

            membershipTable = new RedisMembershipTable(
                Options.Create(new RedisClusteringOptions()
                {
                    ConfigurationOptions = ConfigurationOptions.Parse(GetConnectionString().Result),
                    EntryExpiry = TimeSpan.FromHours(1)
                }),
                this._clusterOptions);

            return membershipTable;
        }

        protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
        {
            return new RedisGatewayListProvider(
                //(RedisMembershipTable)this.membershipTable,
                (RedisMembershipTable)CreateMembershipTable(logger),
                this._gatewayOptions);
        }

        protected override Task<string> GetConnectionString() => Task.FromResult(TestDefaultConfiguration.RedisConnectionString);

        [SkippableFact]
        public async Task GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        [SkippableFact]
        public async Task ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [SkippableFact]
        public async Task InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [SkippableFact]
        public async Task ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [SkippableFact]
        public async Task ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [SkippableFact]
        public async Task UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        [SkippableFact]
        public async Task UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel(false);
        }

        [SkippableFact]
        public async Task UpdateIAmAlive()
        {
            await MembershipTable_UpdateIAmAlive();
        }

        [SkippableFact]
        public async Task CleanupDefunctSiloEntries()
        {
            await MembershipTable_CleanupDefunctSiloEntries(false);
        }
    }
}