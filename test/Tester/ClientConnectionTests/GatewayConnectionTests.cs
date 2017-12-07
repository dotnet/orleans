using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Orleans;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.TestingHost.Utils;

namespace Tester.ClientConnectionTests
{
    public class TestGatewayManager : IGatewayListProvider
    {
        public TimeSpan MaxStaleness => TimeSpan.FromSeconds(1);

        public bool IsUpdatable => true;

        public IList<Uri> Gateways { get; }

        public TestGatewayManager()
        {
            Gateways = new List<Uri>();
        }

        public Task InitializeGatewayListProvider()
        {
            return Task.CompletedTask;
        }

        public Task<IList<Uri>> GetGateways()
        {
            return Task.FromResult(Gateways);
        }
    }

    public class GatewayConnectionTests : TestClusterPerTest
    {
        private readonly OutsideRuntimeClient runtimeClient;

        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions(1)
            {
                ClientConfiguration = {GatewayListRefreshPeriod = TimeSpan.FromMilliseconds(100)}
            };
            return new TestCluster(options).UseClientBuilderFactory(config => CreateClientBuilder(config, options.ClusterConfiguration));
        }

        public static IClientBuilder CreateClientBuilder(ClientConfiguration configuration, ClusterConfiguration clusterConfiguration)
        {
            var primaryGw = clusterConfiguration.Overrides["Primary"].ProxyGatewayEndpoint.ToGatewayUri();
            return new ClientBuilder()
                .UseConfiguration(configuration)
                .ConfigureServices(services => services.AddSingleton(sp =>
                {
                    var gateway = new TestGatewayManager();
                    gateway.Gateways.Add(primaryGw);
                    return gateway;
                }))
                .ConfigureServices(services => services.AddFromExisting<IGatewayListProvider, TestGatewayManager>())
                .ConfigureApplicationParts(parts => parts.AddFromApplicationBaseDirectory())
                .ConfigureLogging(builder => TestingUtils.ConfigureDefaultLoggingBuilder(builder, TestingUtils.CreateTraceFileName(configuration.ClientName, configuration.ClusterId)));
        }

        public GatewayConnectionTests()
        {
            this.runtimeClient = this.Client.ServiceProvider.GetRequiredService<OutsideRuntimeClient>();
        }

        [Fact, TestCategory("Functional")]
        public async Task NoReconnectionToGatewayNotReturnedByManager()
        {
            // Reduce timeout for this test
            this.runtimeClient.SetResponseTimeout(TimeSpan.FromSeconds(1));

            var connectionCount = 0;
            var timeoutCount = 0;

            // Fake Gateway
            var port = HostedCluster.ClusterConfiguration.PrimaryNode.Port + 2;
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);
            var evt = new SocketAsyncEventArgs();
            var gatewayManager = this.runtimeClient.ServiceProvider.GetService<TestGatewayManager>();
            evt.Completed += (sender, args) =>
            {
                connectionCount++;
                gatewayManager.Gateways.Remove(endpoint.ToGatewayUri());
            };

            // Add the fake gateway and wait the refresh from the client
            gatewayManager.Gateways.Add(endpoint.ToGatewayUri());
            await Task.Delay(200);

            using (var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
            {
                // Start the fake gw
                socket.Bind(endpoint);
                socket.Listen(1);
                socket.AcceptAsync(evt);

                // Make a bunch of calls
                for (var i = 0; i < 100; i++)
                {
                    try
                    {
                        var g = this.Client.GetGrain<ISimpleGrain>(i);
                        await g.SetA(i);
                    }
                    catch (TimeoutException)
                    {
                        timeoutCount++;
                    }
                }
                socket.Close();
            }

            // Check that we only connected once to the fake GW
            Assert.Equal(1, connectionCount);
            Assert.Equal(1, timeoutCount);
        }
    }
}