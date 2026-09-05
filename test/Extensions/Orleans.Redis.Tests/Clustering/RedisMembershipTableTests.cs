using System.Collections.Immutable;
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
    /// <summary>
    /// Tests for Orleans membership table operations using Redis as the backing store.
    /// </summary>
    [TestCategory("Redis"), TestCategory("Clustering"), TestCategory("Functional")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    [TestSuite("Functional")]
    [TestProvider("Redis")]
    [TestArea("Membership")]
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

        internal RedisMembershipTable membershipTable = null!;

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

        protected override Task<string> GetConnectionString() => Task.FromResult(TestDefaultConfiguration.RedisConnectionString!);

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

    [TestCategory("BVT")]
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Membership")]
    public class RedisMembershipMetadataContractTests
    {
        [Fact]
        public void ApplyHeartbeat_RepairsMissingMetadata()
        {
            var existing = CreateMetadataEntry(metadata: null);
            var heartbeat = CreateMetadataEntry(
                ImmutableDictionary<string, string>.Empty.Add("region", "west"));

            RedisMembershipTable.ApplyHeartbeat(existing, heartbeat);

            Assert.Equal("west", existing.Metadata!["region"]);
        }

        [Fact]
        public void ApplyHeartbeat_PreservesExistingMetadata()
        {
            var existing = CreateMetadataEntry(
                ImmutableDictionary<string, string>.Empty.Add("region", "existing"));
            var heartbeat = CreateMetadataEntry(
                ImmutableDictionary<string, string>.Empty.Add("region", "replacement"));

            RedisMembershipTable.ApplyHeartbeat(existing, heartbeat);

            Assert.Equal("existing", existing.Metadata!["region"]);
        }

        private static MembershipEntry CreateMetadataEntry(ImmutableDictionary<string, string>? metadata)
            => new()
            {
                SiloAddress = SiloAddress.FromParsableString("127.0.0.1:11111@1"),
                IAmAliveTime = DateTime.UtcNow,
                Metadata = metadata
            };
    }
}
