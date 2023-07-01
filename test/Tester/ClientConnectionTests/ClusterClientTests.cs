using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.ConnectionTransport = ConnectionTransportType.TcpSocket;
        }

        /// <summary>
        /// Ensures that ClusterClient.Connect can be retried.
        /// </summary>
        [Fact]
        public async Task ConnectIsRetryableTest()
        {
            var gateways = await this.HostedCluster.Client.ServiceProvider.GetRequiredService<IGatewayListProvider>().GetGateways();
            var gwEndpoint = gateways.First();

            // Create a client with no gateway endpoint and then add a gateway endpoint when the client fails to connect.
            var gatewayProvider = new MockGatewayListProvider();
            var exceptions = new List<Exception>();

            Task<bool> RetryFunc(Exception exception, CancellationToken cancellationToken)
            {
                Assert.IsType<SiloUnavailableException>(exception);
                exceptions.Add(exception);
                gatewayProvider.Gateways = new List<Uri> { gwEndpoint }.AsReadOnly();
                return Task.FromResult(true);
            }

            using var host = new HostBuilder().UseOrleansClient((ctx, clientBuilder) =>
                {
                    clientBuilder
                        .Configure<ClusterOptions>(options =>
                        {
                            var existingClientOptions = this.HostedCluster.ServiceProvider
                                .GetRequiredService<IOptions<ClusterOptions>>().Value;
                            options.ClusterId = existingClientOptions.ClusterId;
                            options.ServiceId = existingClientOptions.ServiceId;
                        })
                        .ConfigureServices(services => services.AddSingleton<IGatewayListProvider>(gatewayProvider))
                        .UseConnectionRetryFilter(RetryFunc);
                })
                .Build();

            var client = host.Services.GetRequiredService<IClusterClient>();

            await host.StartAsync();
            Assert.Single(exceptions);
            await host.StopAsync();
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