using System.Net;
using Microsoft.Extensions.Hosting;
using Xunit;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Tester.Redis.Persistence
{
    [TestCategory("Redis"), TestCategory("Persistence"), TestCategory("Functional")]
    public class RedisPersistenceSetupTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData("123")]
        public void StorageOptionsValidator(string connectionString)
        {
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
                            options.ConnectionString = connectionString;
                        }));
                }).Build();

            Assert.Throws<OrleansConfigurationException>(() => host.Start());
        }
    }
}