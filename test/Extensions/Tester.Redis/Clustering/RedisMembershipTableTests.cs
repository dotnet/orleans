using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Clustering.Redis;
using Orleans.Configuration;
using Orleans.Messaging;
using System.Threading.Tasks;
using Tester.Redis.Utility;
using Xunit;
using Xunit.Abstractions;
using UnitTests.MembershipTests;
using System;
using TestExtensions;
using UnitTests;
using Tester.Redis.Persistence;

namespace Tester.Redis.Clustering
{
    // <summary>
    // Tests for operation of Orleans Membership Table using Redis
    // </summary>
    [TestCategory("Redis"), TestCategory("Clustering"), TestCategory("Functional")]
    public class RedisMembershipTableTests : MembershipTableTestsBase
    {
        public RedisMembershipTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, CreateFilters())
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
            membershipTable = new RedisMembershipTable(
                Options.Create(new RedisClusteringOptions() { ConnectionString = GetConnectionString().Result }),
                this.clusterOptions);

            return membershipTable;
        }

        protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
        {
            return new RedisGatewayListProvider(
                //(RedisMembershipTable)this.membershipTable,
                (RedisMembershipTable)CreateMembershipTable(logger),
                this.gatewayOptions);
        }

        protected override Task<string> GetConnectionString() => Task.FromResult(TestDefaultConfiguration.RedisConnectionString);

        [Fact]
        public async Task GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        [Fact]
        public async Task ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [Fact]
        public async Task InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [Fact]
        public async Task ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [Fact]
        public async Task ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [Fact]
        public async Task UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        [Fact]
        public async Task UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel(false);
        }

        [Fact]
        public async Task UpdateIAmAlive()
        {
            await MembershipTable_UpdateIAmAlive();
        }

        [Fact]
        public async Task CleanupDefunctSiloEntries()
        {
            await MembershipTable_CleanupDefunctSiloEntries(false);
        }
    }
}