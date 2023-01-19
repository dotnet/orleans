using System.Net;
using Microsoft.Extensions.Hosting;
using Xunit;
using Orleans.Configuration;
using Orleans.Runtime;
using StackExchange.Redis;
using TestExtensions;

namespace Tester.Redis.Persistence
{
    [TestCategory("Redis"), TestCategory("Persistence"), TestCategory("Functional")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class RedisPersistenceSetupTests
    {
        [SkippableTheory]
        [InlineData(null)]
        [InlineData("localhost:1234")]
        public void StorageOptionsValidator(string connectionString)
        {
            TestUtils.CheckForRedis();

            var siloPort = 11111;
            int gatewayPort = 30000;
            var siloAddress = IPAddress.Loopback;

            var host = Host.CreateDefaultBuilder()
                .UseOrleans((ctx, builder) => {
                    builder.Configure<ClusterOptions>(options => options.ClusterId = "TESTCLUSTER")
                        .UseDevelopmentClustering(options => options.PrimarySiloEndpoint = new IPEndPoint(siloAddress, siloPort))
                        .ConfigureEndpoints(siloAddress, siloPort, gatewayPort)
                        .AddRedisGrainStorage("Redis", optionsBuilder => optionsBuilder.Configure(options =>
                        {
                            if (connectionString is not null)
                            {
                                options.ConfigurationOptions = ConfigurationOptions.Parse(connectionString);
                            }
                        }));
                }).Build();

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Assert.Throws<OrleansConfigurationException>(() => host.Start());
            }
        }
    }
}