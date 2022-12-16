using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;
using StackExchange.Redis;
using Tester.Redis.Utility;
using TestExtensions;
using TestExtensions.Runners;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace Tester.Redis.Persistence
{
    [TestCategory("Redis"), TestCategory("Persistence"), TestCategory("Functional")]
    public partial class RedisPersistenceGrainTests : GrainPersistenceTestsRunner, IClassFixture<RedisPersistenceGrainTests.Fixture>
    {
        public static Guid ServiceId = Guid.NewGuid();
        public static string ConnectionStringKey = "ConnectionString";
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
                builder.Options.ServiceId = ServiceId.ToString();
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
                            //.UseRedisClustering(options =>
                            //{
                            //    options.ConnectionString = connectionString;
                            //})
                            .AddRedisGrainStorage("GrainStorageForTest", options =>
                            {
                                options.ConnectionString = connectionString;
                            })
                            .AddMemoryGrainStorage("MemoryStore");
                    });
                }
            }
        }

        private Fixture fixture;

        public RedisPersistenceGrainTests(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
        {
            this.fixture = fixture;
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
    }
}
