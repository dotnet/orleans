using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Tasks;
using TestExtensions;
using Xunit;

namespace Tester.ClientConnectionTests
{
    public class StallConnectionTests : TestClusterPerTest
    {
        private static TimeSpan Timeout = TimeSpan.FromSeconds(10);

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            builder.AddClientBuilderConfigurator<ClientConfigurator>();
        }

        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.Configure<ConnectionOptions>(options => options.OpenConnectionTimeout = Timeout);
            }
        }

        public class ClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.Configure<ClientMessagingOptions>(options => options.ResponseTimeout = Timeout);
            }
        }

        [Fact, TestCategory("Functional")]
        public async Task ConnectToGwAfterStallConnectionOpened()
        {
            Socket stalledSocket;
            var gwEndpoint = this.HostedCluster.Primary.GatewayAddress.Endpoint;

            // Close current client connection
            await this.Client.ServiceProvider.GetRequiredService<IHost>().StopAsync();

            // Stall connection to GW
            using (stalledSocket = new Socket(gwEndpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
            {
                await stalledSocket.ConnectAsync(gwEndpoint);

                // Try to reconnect to GW
                var stopwatch = Stopwatch.StartNew();
                await this.HostedCluster.InitializeClientAsync();
                stopwatch.Stop();

                // Check that we were able to connect before the first connection timeout
                Assert.True(stopwatch.Elapsed < Timeout);

                stalledSocket.Disconnect(true);
            }
        }

        [Fact, TestCategory("Functional")]
        public async Task SiloJoinAfterStallConnectionOpened()
        {
            Socket stalledSocket;
            var siloEndpoint = this.HostedCluster.Primary.SiloAddress.Endpoint;

            // Stall connection to GW
            using (stalledSocket = new Socket(siloEndpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
            {
                await stalledSocket.ConnectAsync(siloEndpoint);

                // Try to add a new silo in the cluster
                this.HostedCluster.StartAdditionalSilo();

                // Wait for the silo to join the cluster
                Assert.True(await WaitForClusterSize(2));

                stalledSocket.Disconnect(true);
            }
        }

        private async Task<bool> WaitForClusterSize(int expectedSize)
        {
            var mgmtGrain = this.Client.GetGrain<IManagementGrain>(0);
            var timeout = TestCluster.GetLivenessStabilizationTime(new Orleans.Configuration.ClusterMembershipOptions());
            var stopWatch = Stopwatch.StartNew();
            do
            {
                var hosts = await mgmtGrain.GetHosts();
                if (hosts.Count == expectedSize)
                {
                    stopWatch.Stop();
                    return true;
                }
                await Task.Delay(500);
            }
            while (stopWatch.Elapsed < timeout);
            return false;
        }
    }
}
