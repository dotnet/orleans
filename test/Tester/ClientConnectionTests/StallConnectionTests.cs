﻿using Orleans.Runtime;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private static TimeSpan Timeout = TimeSpan.FromSeconds(10);

        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions(1);
            options.ClusterConfiguration.Globals.OpenConnectionTimeout = Timeout;
            options.ClientConfiguration.ResponseTimeout = Timeout;
            return new TestCluster(options);
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
                var stopwatch = Stopwatch.StartNew();
                this.HostedCluster.InitializeClient();
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
            var siloEndpoint = this.HostedCluster.Primary.NodeConfiguration.Endpoint;

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
            var timeout = TestCluster.GetLivenessStabilizationTime(this.HostedCluster.ClusterConfiguration.Globals);
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
