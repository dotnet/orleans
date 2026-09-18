using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Clustering.Redis;
using Orleans.Messaging;
using Orleans.Runtime;
using Xunit;
using UnitTests.MembershipTests;
using TestExtensions;
using UnitTests;
using StackExchange.Redis;
using StackExchange.Redis.Profiling;

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
            await MembershipTable_UpdateRowInParallel();
        }

        [Fact]
        public async Task UpdateIAmAlive()
        {
            await MembershipTable_UpdateIAmAlive();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task UpdateIAmAlive_OneCommandPreservesOtherBytesAndExpiry(bool hasVersion, bool expires)
        {
            using var connection = await ConnectionMultiplexer.ConnectAsync(await GetConnectionString());
            using var table = new RedisMembershipTable(
                Options.Create(new RedisClusteringOptions { CreateMultiplexer = _ => Task.FromResult(((IConnectionMultiplexer)connection, true)) }),
                _clusterOptions);
            var token = TestContext.Current.CancellationToken;
            await table.InitializeMembershipTableAsync(false, token);
            var entry = new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                Status = SiloStatus.Active,
                HostName = "host,\"IAmAliveTime\":\"escaped\",\\\r\n",
                SiloName = "silo-\u00e9",
                StartTime = DateTime.UnixEpoch,
                IAmAliveTime = DateTime.UnixEpoch,
                ProxyPort = 30000,
                UpdateZone = 12,
                FaultZone = 34
            };
            entry.SuspectTimes = hasVersion ? [Tuple.Create(entry.SiloAddress, entry.StartTime.AddTicks(1))] : [];
            var version = (await table.ReadAllAsync(token)).Version.Next();
            Assert.True(await table.InsertRowAsync(entry, version, token));
            var database = connection.GetDatabase();
            var key = RedisClusteringOptions.DefaultCreateRedisKey(_clusterOptions.Value);
            var rowKey = entry.SiloAddress.ToString();
            var original = (await database.HashGetAsync(key, rowKey)).ToString();
            var before = original[..^1] + ",\"FutureInteger\":9007199254740993,\"FutureArray\":[],\"FutureNull\":null}";
            await database.HashSetAsync(key, rowKey, before);
            if (!hasVersion)
            {
                Assert.True(await database.HashDeleteAsync(key, "Version"));
            }

            DateTime? expiry = expires ? DateTime.UtcNow.AddHours(1) : null;
            Assert.True(await database.KeyExpireAsync(key, expiry));
            var expiryBefore = await database.KeyExpireTimeAsync(key);
            Assert.Equal(expires, expiryBefore.HasValue);
            var versionBefore = await database.HashGetAsync(key, "Version");
            var heartbeat = new MembershipEntry
            {
                SiloAddress = entry.SiloAddress,
                IAmAliveTime = entry.IAmAliveTime.AddTicks(1234567)
            };
            ProfilingSession? activeProfile = null;
            connection.RegisterProfiler(() => activeProfile);
            var profile = new ProfilingSession();
            activeProfile = profile;

            await table.UpdateIAmAliveAsync(heartbeat, token);

            activeProfile = null;
            Assert.Equal("EVAL", Assert.Single(profile.FinishProfiling()).Command);
            var expected = before.Replace(
                "\"IAmAliveTime\":" + JsonConvert.SerializeObject(entry.IAmAliveTime, JsonSettings.JsonSerializerSettings),
                "\"IAmAliveTime\":" + JsonConvert.SerializeObject(heartbeat.IAmAliveTime, JsonSettings.JsonSerializerSettings),
                StringComparison.Ordinal);
            Assert.NotEqual(before, expected);
            var after = (await database.HashGetAsync(key, rowKey)).ToString();
            Assert.Equal(expected, after);
            Assert.Equal(heartbeat.IAmAliveTime, JsonConvert.DeserializeObject<MembershipEntry>(after, JsonSettings.JsonSerializerSettings)!.IAmAliveTime);
            Assert.Equal(versionBefore, await database.HashGetAsync(key, "Version"));
            Assert.Equal(expiryBefore, await database.KeyExpireTimeAsync(key));
        }

        [Fact]
        public async Task CleanupDefunctSiloEntries()
        {
            await MembershipTable_CleanupDefunctSiloEntries();
        }
    }
}
