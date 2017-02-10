using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.ClientConnectionTests
{
    public class ClientDisconnectionEventTests : TestClusterPerTest
    {
        public override TestCluster CreateTestCluster()
        {
            return new TestCluster(new TestClusterOptions(2));
        }

        [Fact, TestCategory("BVT")]
        public async Task EventSendWhenDisconnectedFromCluster()
        {
            var runtime = this.HostedCluster.ServiceProvider.GetRequiredService<OutsideRuntimeClient>();

            var semaphore = new SemaphoreSlim(0, 1);
            GrainClient.ClusterConnectionLost += (sender, args) => semaphore.Release();

            // Burst lot of call, to be sure that we are connected to all silos
            for (int i = 0; i < 100; i++)
            {
                var grain = GrainFactory.GetGrain<ITestGrain>(i);
                await grain.SetLabel(i.ToString());
            }

            runtime.Disconnect();

            Assert.True(await semaphore.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }
}
