using Orleans.Runtime;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;
using Xunit;

namespace Tester.ClientConnectionTests
{
    public class StallConnectionTests : TestClusterPerTest
    {
        public override TestCluster CreateTestCluster()
        {
            return new TestCluster(new TestClusterOptions(1));
        }

        [Fact, TestCategory("Functional")]
        public async Task ConnectToGwAfterStallConnectionOpened()
        {
            Socket stalledSocket;
            var gwEndpoint = this.HostedCluster.Primary.NodeConfiguration.ProxyGatewayEndpoint;

            // Close current client connection
            await this.Client.Close();

            // Stall connection to GW
            using (stalledSocket = new Socket(gwEndpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
            {
                await stalledSocket.ConnectAsync(gwEndpoint);

                // Try to reconnect to GW
                this.HostedCluster.InitializeClient();

                stalledSocket.Disconnect(true);
            }
        }

        [Fact, TestCategory("Functional")]
        public async Task SiloJoinAfterStallConnectionOpened()
        {
            Socket stalledSocket;
            var siloEndpoint = this.HostedCluster.Primary.NodeConfiguration.Endpoint;

            // Stall connection to GW
            using (stalledSocket = new Socket(siloEndpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
            {
                await stalledSocket.ConnectAsync(siloEndpoint);

                // Try to add a new silo in the cluster
                this.HostedCluster.StartAdditionalSilo();

                // Wait for the silo to join the cluster
                await this.HostedCluster.WaitForLivenessToStabilizeAsync();

                var mgmtGrain = this.Client.GetGrain<IManagementGrain>(0);
                var hosts = await mgmtGrain.GetHosts();

                Assert.Equal(2, hosts.Count);

                stalledSocket.Disconnect(true);
            }
        }
    }
}
