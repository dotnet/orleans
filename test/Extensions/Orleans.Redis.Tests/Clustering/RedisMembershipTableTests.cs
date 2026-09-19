using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using NSubstitute;
using Orleans.Clustering.Redis;
using Orleans.Clustering.TestKit;
using Orleans.Configuration;
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
        private readonly Dictionary<string, RedisKey> _conformanceKeys = [];

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
            membershipTable = (RedisMembershipTable)CreateMembershipTable(logger, _clusterOptions);
            return membershipTable;
        }

        protected override IMembershipTable CreateMembershipTable(ILogger logger, IOptions<ClusterOptions> clusterOptions)
        {
            TestUtils.CheckForRedis();

            return new RedisMembershipTable(
                Options.Create(new RedisClusteringOptions()
                {
                    ConfigurationOptions = ConfigurationOptions.Parse(connectionString),
                    EntryExpiry = TimeSpan.FromHours(1)
                }),
                clusterOptions);
        }

        protected override MembershipTableTestHandle CreateConformanceHandle(ILogger logger, IOptions<ClusterOptions> clusterOptions)
        {
            _conformanceKeys[clusterOptions.Value.ClusterId] = RedisClusteringOptions.DefaultCreateRedisKey(clusterOptions.Value);
            return base.CreateConformanceHandle(logger, clusterOptions);
        }

        protected override MembershipTableTestFixture CreateConformanceFixture()
            => CreateConformanceFixture(IsConformanceClusterDeletedAsync);

        private async ValueTask<bool> IsConformanceClusterDeletedAsync(string clusterId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = _conformanceKeys[clusterId];
            await using var client = await ConnectionMultiplexer.ConnectAsync(ConfigurationOptions.Parse(connectionString));
            cancellationToken.ThrowIfCancellationRequested();
            return !await client.GetDatabase().KeyExistsAsync(key, CommandFlags.DemandMaster);
        }

        protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
        {
            return new RedisGatewayListProvider(
                membershipTable,
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
            await InitializeLegacyMembershipTableAsync(TestContext.Current.CancellationToken);
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CanonicalWrite_UsesOriginalTokensAfterOwnerHeartbeat(bool insert)
        {
            await InitializeLegacyMembershipTableAsync(TestContext.Current.CancellationToken);
            using var connection = await ConnectionMultiplexer.ConnectAsync(await GetConnectionString());
            using var table = new RedisMembershipTable(
                Options.Create(new RedisClusteringOptions { CreateMultiplexer = _ => Task.FromResult(((IConnectionMultiplexer)connection, true)) }),
                _clusterOptions);
            var token = TestContext.Current.CancellationToken;
            await table.InitializeMembershipTableAsync(false, token);
            var owner = new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                Status = SiloStatus.Active,
                StartTime = DateTime.UnixEpoch,
                IAmAliveTime = DateTime.UnixEpoch,
                HostName = "owner",
                SiloName = "owner"
            };
            Assert.True(await table.InsertRowAsync(owner, (await table.ReadAllAsync(token)).Version.Next(), token));
            var snapshot = await table.ReadRowAsync(owner.SiloAddress, token);
            var rowEtag = Assert.Single(snapshot.Members).Item2;
            var version = snapshot.Version.Next();
            var heartbeat = new MembershipEntry { SiloAddress = owner.SiloAddress, IAmAliveTime = owner.IAmAliveTime.AddTicks(1234567) };
            await table.UpdateIAmAliveAsync(heartbeat, token);
            var entry = snapshot.Members[0].Item1;
            if (insert)
            {
                entry.SiloAddress = SiloAddress.New(IPAddress.Loopback, 22222, 1);
            }

            entry.Status = SiloStatus.Dead;
            entry.SuspectTimes = [Tuple.Create(owner.SiloAddress, heartbeat.IAmAliveTime)];
            ProfilingSession? activeProfile = null;
            connection.RegisterProfiler(() => activeProfile);
            var profile = new ProfilingSession();
            activeProfile = profile;

            var result = insert
                ? await table.InsertRowAsync(entry, version, token)
                : await table.UpdateRowAsync(entry, rowEtag, version, token);

            activeProfile = null;
            Assert.True(result);
            Assert.Equal("EVAL", Assert.Single(profile.FinishProfiling()).Command);
            var after = await table.ReadRowAsync(entry.SiloAddress, token);
            var persisted = Assert.Single(after.Members).Item1;
            Assert.Equal(version.Version, after.Version.Version);
            Assert.Equal(SiloStatus.Dead, persisted.Status);
            Assert.Equal(entry.SuspectTimes, persisted.SuspectTimes);
            if (insert)
            {
                Assert.Equal(heartbeat.IAmAliveTime, Assert.Single((await table.ReadRowAsync(owner.SiloAddress, token)).Members).Item1.IAmAliveTime);
            }

            entry.Status = SiloStatus.Active;
            Assert.False(await table.UpdateRowAsync(entry, rowEtag, version, token));
            var unchanged = await table.ReadRowAsync(entry.SiloAddress, token);
            Assert.Equal(after.Version, unchanged.Version);
            Assert.Equal(SiloStatus.Dead, Assert.Single(unchanged.Members).Item1.Status);
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(false, 1)]
        [InlineData(false, 10)]
        [InlineData(true, 0)]
        [InlineData(true, 1)]
        [InlineData(true, 10)]
        public async Task CanonicalWrite_RejectsNonSequentialVersionInOneCommand(bool insert, int proposedVersion)
        {
            await InitializeLegacyMembershipTableAsync(TestContext.Current.CancellationToken);
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
                StartTime = DateTime.UnixEpoch,
                IAmAliveTime = DateTime.UnixEpoch,
                HostName = "host",
                SiloName = "silo"
            };
            Assert.True(await table.InsertRowAsync(entry, (await table.ReadAllAsync(token)).Version.Next(), token));
            var snapshot = await table.ReadRowAsync(entry.SiloAddress, token);
            var rowEtag = Assert.Single(snapshot.Members).Item2;
            if (insert)
            {
                entry.SiloAddress = SiloAddress.New(IPAddress.Loopback, 22222, 1);
            }

            entry.Status = SiloStatus.Dead;
            var database = connection.GetDatabase();
            var key = RedisClusteringOptions.DefaultCreateRedisKey(_clusterOptions.Value);
            var before = (await database.HashGetAllAsync(key)).ToDictionary(row => row.Name, row => row.Value);
            var expiry = await database.KeyExpireTimeAsync(key);
            var invalid = new TableVersion(proposedVersion, snapshot.Version.VersionEtag);
            ProfilingSession? activeProfile = null;
            connection.RegisterProfiler(() => activeProfile);
            var profile = new ProfilingSession();
            activeProfile = profile;

            var result = insert
                ? await table.InsertRowAsync(entry, invalid, token)
                : await table.UpdateRowAsync(entry, rowEtag, invalid, token);

            activeProfile = null;
            Assert.False(result);
            Assert.Equal("EVAL", Assert.Single(profile.FinishProfiling()).Command);
            var after = (await database.HashGetAllAsync(key)).ToDictionary(row => row.Name, row => row.Value);
            Assert.Equal(before.Count, after.Count);
            foreach (var row in before)
            {
                Assert.Equal(row.Value, after[row.Key]);
            }

            Assert.Equal(expiry, await database.KeyExpireTimeAsync(key));
            Assert.True(insert
                ? await table.InsertRowAsync(entry, snapshot.Version.Next(), token)
                : await table.UpdateRowAsync(entry, rowEtag, snapshot.Version.Next(), token));
            var updated = await table.ReadRowAsync(entry.SiloAddress, token);
            Assert.Equal(snapshot.Version.Version + 1, updated.Version.Version);
            Assert.Equal(SiloStatus.Dead, Assert.Single(updated.Members).Item1.Status);
        }

        [Fact]
        public async Task CleanupDefunctSiloEntries()
        {
            await MembershipTable_CleanupDefunctSiloEntries();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Cleanup_OneScanAndAtomicCandidateCommands_PreserveRacingChanges(bool refreshCandidate)
        {
            await InitializeLegacyMembershipTableAsync(TestContext.Current.CancellationToken);
            using var connection = await ConnectionMultiplexer.ConnectAsync(await GetConnectionString());
            using var table = new RedisMembershipTable(
                Options.Create(new RedisClusteringOptions { CreateMultiplexer = _ => Task.FromResult(((IConnectionMultiplexer)connection, true)) }),
                _clusterOptions);
            var token = TestContext.Current.CancellationToken;
            await table.InitializeMembershipTableAsync(false, token);
            var cutoff = DateTime.UnixEpoch.AddSeconds(2);
            var entries = Enumerable.Range(1, 4).Select(index => new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11110 + index, 1),
                Status = index == 3 ? SiloStatus.Active : SiloStatus.Dead,
                StartTime = DateTime.UnixEpoch,
                IAmAliveTime = DateTime.UnixEpoch.AddSeconds(1),
                HostName = "host",
                SiloName = $"silo-{index}"
            }).ToArray();
            entries[3].SuspectTimes = [Tuple.Create(entries[2].SiloAddress, cutoff)];
            foreach (var entry in entries)
            {
                Assert.True(await table.InsertRowAsync(entry, (await table.ReadAllAsync(token)).Version.Next(), token));
            }

            var database = connection.GetDatabase();
            var key = RedisClusteringOptions.DefaultCreateRedisKey(_clusterOptions.Value);
            var before = (await database.HashGetAllAsync(key)).ToDictionary(row => row.Name, row => row.Value);
            var expiry = await database.KeyExpireTimeAsync(key);
            Assert.NotNull(expiry);
            var cleanupDatabase = Substitute.For<IDatabase>();
            var cleanupConnection = Substitute.For<IConnectionMultiplexer>();
            cleanupConnection.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(cleanupDatabase);
            cleanupDatabase.HashGetAllAsync(key).Returns(async _ =>
            {
                var snapshot = await database.HashGetAllAsync(key);
                var owner = refreshCandidate ? entries[0] : entries[2];
                owner.IAmAliveTime = cutoff;
                await table.UpdateIAmAliveAsync(owner, token);
                return snapshot;
            });
            cleanupDatabase.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), CommandFlags.NoScriptCache)
                .Returns(call => database.ScriptEvaluateAsync(
                    call.ArgAt<string>(0), call.ArgAt<RedisKey[]>(1), call.ArgAt<RedisValue[]>(2), CommandFlags.NoScriptCache));
            using var cleanupTable = new RedisMembershipTable(
                Options.Create(new RedisClusteringOptions { CreateMultiplexer = _ => Task.FromResult((cleanupConnection, true)) }),
                _clusterOptions);
            await cleanupTable.InitializeMembershipTableAsync(false, token);
            ProfilingSession? activeProfile = null;
            connection.RegisterProfiler(() => activeProfile);
            var profile = new ProfilingSession();
            activeProfile = profile;

            await cleanupTable.CleanupDefunctSiloEntriesAsync(cutoff, token);

            activeProfile = null;
            Assert.Equal(new[] { "HGETALL", "EVAL", "EVAL", "EVAL" }, profile.FinishProfiling().Select(command => command.Command));
            Assert.Equal(new[] { nameof(IDatabase.HashGetAllAsync), nameof(IDatabase.ScriptEvaluateAsync), nameof(IDatabase.ScriptEvaluateAsync) },
                cleanupDatabase.ReceivedCalls().Select(call => call.GetMethodInfo().Name));
            var after = (await database.HashGetAllAsync(key)).ToDictionary(row => row.Name, row => row.Value);
            before.Remove(entries[1].SiloAddress.ToString());
            if (!refreshCandidate)
            {
                before.Remove(entries[0].SiloAddress.ToString());
            }

            var updatedOwner = refreshCandidate ? entries[0] : entries[2];
            before[updatedOwner.SiloAddress.ToString()] = JsonConvert.SerializeObject(updatedOwner, JsonSettings.JsonSerializerSettings);
            Assert.Equal(before.Count, after.Count);
            foreach (var row in before)
            {
                Assert.Equal(row.Value, after[row.Key]);
            }

            Assert.Equal(expiry, await database.KeyExpireTimeAsync(key));
        }
    }
}
