using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost;
using StackExchange.Redis;
using TestExtensions;
using TestExtensions.Runners;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace Tester.Redis.Persistence
{
    [TestCategory("Redis"), TestCategory("Persistence"), TestCategory("Functional")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class RedisPersistenceGrainTests : GrainPersistenceTestsRunner, IClassFixture<RedisPersistenceGrainTests.Fixture>
    {
        public static readonly string ServiceId = Guid.NewGuid().ToString("N");
        public const string ConnectionStringKey = "ConnectionString";
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 4;
                builder.Options.UseTestClusterMembership = true;
                builder.ConfigureHostConfiguration(configBuilder => configBuilder.AddInMemoryCollection(
                    new Dictionary<string, string>
                    {
                        {ConnectionStringKey, TestDefaultConfiguration.RedisConnectionString}
                    }));
                builder.Options.ServiceId = ServiceId;
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
                builder.AddClientBuilderConfigurator<GatewayConnectionTests.ClientBuilderConfigurator>();
            }

            private class MySiloBuilderConfigurator : IHostConfigurator
            {
                public void Configure(IHostBuilder hostBuilder)
                {
                    var connectionString = hostBuilder.GetConfiguration()[ConnectionStringKey];
                    hostBuilder.UseOrleans((ctx, siloBuilder) =>
                    {
                        siloBuilder
                            .AddRedisGrainStorage("GrainStorageForTest", options =>
                            {
                                options.ConfigurationOptions = ConfigurationOptions.Parse(connectionString);
                                options.EntryExpiry = TimeSpan.FromHours(1);
                            })
                            .AddMemoryGrainStorage("MemoryStore");
                    });
                }
            }

            protected override void CheckPreconditionsOrThrow() => TestUtils.CheckForRedis();
        }

        private Fixture fixture;

        public RedisPersistenceGrainTests(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
        {
            try
            {
                this.fixture = fixture;
            }
            catch(OrleansConfigurationException) { }

            this.fixture.EnsurePreconditionsMet();

            var redisOptions = ConfigurationOptions.Parse(TestDefaultConfiguration.RedisConnectionString);
            var redis = ConnectionMultiplexer.ConnectAsync(redisOptions).Result;
            this.database = redis.GetDatabase();

            this.state = new()
            {
                DateTimeValue = DateTime.UtcNow,
                GuidValue = Guid.NewGuid(),
                IntValue = 12345,
                StringValue = "string value",
                GrainValue = fixture.GrainFactory.GetGrain<IGrainStorageGenericGrain<GrainState>>(999)
            };
        }

        // Redis specific tests

        private GrainState state;
        private IDatabase database;

        [SkippableFact]
        public async Task Redis_InitializeWithNoStateTest()
        {
            var grain = fixture.GrainFactory.GetGrain<IGrainStorageGenericGrain<GrainState>>(0);
            var result = await grain.DoRead();

            //Assert.NotNull(result);
            Assert.Equal(default(GrainState), result);
            //Assert.Equal(default(string), result.StringValue);
            //Assert.Equal(default(int), result.IntValue);
            //Assert.Equal(default(DateTime), result.DateTimeValue);
            //Assert.Equal(default(Guid), result.GuidValue);
            //Assert.Equal(default(ITestGrain), result.GrainValue);
        }

        [SkippableFact]
        public async Task Redis_TestStaticIdentifierGrains()
        {
            var grain = fixture.GrainFactory.GetGrain<IGrainStorageGenericGrain<GrainState>>(12345);
            await grain.DoWrite(state);

            var grain2 = fixture.GrainFactory.GetGrain<IGrainStorageGenericGrain<GrainState>>(12345);
            var result = await grain2.DoRead();
            Assert.Equal(result.StringValue, state.StringValue);
            Assert.Equal(result.IntValue, state.IntValue);
            Assert.Equal(result.DateTimeValue, state.DateTimeValue);
            Assert.Equal(result.GuidValue, state.GuidValue);
            Assert.Equal(result.GrainValue, state.GrainValue);
        }

        [SkippableFact]
        public async Task Redis_TestRedisScriptCacheClearBeforeGrainWriteState()
        {
            var grain = fixture.GrainFactory.GetGrain<IGrainStorageGenericGrain<GrainState>>(1111);

            var info = (string)await database.ExecuteAsync("INFO");
            var versionString = Regex.Match(info, @"redis_version:[\s]*([^\s]+)").Groups[1].Value;
            var version = Version.Parse(versionString);
            if (version >= Version.Parse("6.2.0"))
            {
                await database.ExecuteAsync("SCRIPT", "FLUSH", "SYNC");
            }
            else
            {
                await database.ExecuteAsync("SCRIPT", "FLUSH");
            }

            await grain.DoWrite(state);

            var result = await grain.DoRead();
            Assert.Equal(result.StringValue, state.StringValue);
            Assert.Equal(result.IntValue, state.IntValue);
            Assert.Equal(result.DateTimeValue, state.DateTimeValue);
            Assert.Equal(result.GuidValue, state.GuidValue);
            Assert.Equal(result.GrainValue, state.GrainValue);
        }

        [SkippableFact]
        public async Task Redis_DoubleActivationETagConflictSimulation()
        {
            var grain = fixture.GrainFactory.GetGrain<IGrainStorageGenericGrain<GrainState>>(54321);
            var data = await grain.DoRead();

            var key = $"{ServiceId}/state/{grain.GetGrainId()}/state";
            await database.HashSetAsync(key, new[] { new HashEntry("etag", "derp") });

            await Assert.ThrowsAsync<InconsistentStateException>(() => grain.DoWrite(state));
        }
    }
}
