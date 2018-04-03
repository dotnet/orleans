using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Tester.ClientConnectionTests
{
    [TestCategory("Functional")]
    public class ClusterClientTests : TestClusterPerTest
    {
        /// <summary>
        /// Ensures that <see "ClusterClient.Connect" /> can be retried.
        /// </summary>
        [Fact]
        public async Task ConnectIsRetryableTest()
        {
            var gwEndpoint = this.HostedCluster.Client.Configuration().Gateways.First();

            // Create a client with no gateway endpoint and then add a gateway endpoint when the client fails to connect.
            var gatewayProvider = new MockGatewayListProvider();
            var client = new ClientBuilder()
                .Configure<ClusterOptions>(options =>
                {
                    var existingClientOptions = this.HostedCluster.ServiceProvider.GetRequiredService<IOptions<ClusterOptions>>().Value;
                    options.ClusterId = existingClientOptions.ClusterId;
                    options.ServiceId = existingClientOptions.ServiceId;
                })
                .ConfigureServices(services => services.AddSingleton<IGatewayListProvider>(gatewayProvider))
                .Build();
            var exceptions = new List<Exception>();

            Task<bool> RetryFunc(Exception exception)
            {
                Assert.IsType<OrleansException>(exception);
                exceptions.Add(exception);
                gatewayProvider.Gateways = new List<Uri> {gwEndpoint.ToGatewayUri()}.AsReadOnly();
                return Task.FromResult(true);
            }

            await client.Connect(RetryFunc);
            Assert.Single(exceptions);
        }

        public class MockGatewayListProvider : IGatewayListProvider
        {
            public ReadOnlyCollection<Uri> Gateways { get; set; } = new ReadOnlyCollection<Uri>(new List<Uri>());

            public Task InitializeGatewayListProvider() => Task.CompletedTask;

            public Task<IList<Uri>> GetGateways() => Task.FromResult<IList<Uri>>(this.Gateways);

            public TimeSpan MaxStaleness => TimeSpan.FromSeconds(30);

            public bool IsUpdatable => true;
        }
    }
}