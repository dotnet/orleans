using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester
{
    [TestCategory("Functional")]
    public class LocalhostClusterTests
    {
        /// <summary>
        /// Tests that <see cref="CoreHostingExtensions.UseLocalhostClustering"/> works for single silo clusters.
        /// </summary>
        [Fact]
        public async Task LocalhostSiloTest()
        {
            using var portAllocator = new TestClusterPortAllocator();
            var (siloPort, gatewayPort) = portAllocator.AllocateConsecutivePortPairs(1);
            var host = new HostBuilder().UseOrleans(siloBuilder =>
            {
                siloBuilder.AddMemoryGrainStorage("MemoryStore")
                .UseLocalhostClustering(siloPort, gatewayPort);
            }).Build();

            var client = new ClientBuilder()
                .UseLocalhostClustering(gatewayPort)
                .Build();
            try
            {
                await host.StartAsync();
                await client.Connect();
                var grain = client.GetGrain<IEchoGrain>(Guid.NewGuid());
                var result = await grain.Echo("test");
                Assert.Equal("test", result);
            }
            finally
            {
                await OrleansTaskExtentions.SafeExecute(() => host.StopAsync());
                await OrleansTaskExtentions.SafeExecute(() => client.Close());
                Utils.SafeExecute(() => host.Dispose());
                Utils.SafeExecute(() => client.Close());
            }
        }

        /// <summary>
        /// Tests that <see cref="CoreHostingExtensions.UseLocalhostClustering"/> works for multi-silo clusters.
        /// </summary>
        [Fact]
        public async Task LocalhostClusterTest()
        {
            using var portAllocator = new TestClusterPortAllocator();
            var (baseSiloPort, baseGatewayPort) = portAllocator.AllocateConsecutivePortPairs(2);
            var silo1 = new HostBuilder().UseOrleans(siloBuilder =>
            {
                siloBuilder
                .AddMemoryGrainStorage("MemoryStore")
                .UseLocalhostClustering(baseSiloPort, baseGatewayPort);
            }).Build();

            var silo2 = new HostBuilder().UseOrleans(siloBuilder =>
            {
                siloBuilder
                .AddMemoryGrainStorage("MemoryStore")
                .UseLocalhostClustering(baseSiloPort + 1, baseGatewayPort + 1, new IPEndPoint(IPAddress.Loopback, baseSiloPort));
            }).Build();

            var client = new ClientBuilder()
                .UseLocalhostClustering(new[] { baseGatewayPort, baseGatewayPort + 1})
                .Build();

            try
            {
                await Task.WhenAll(silo1.StartAsync(), silo2.StartAsync());

                await client.Connect();
                var grain = client.GetGrain<IEchoGrain>(Guid.NewGuid());
                var result = await grain.Echo("test");
                Assert.Equal("test", result);
            }
            finally
            {
                var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                Utils.SafeExecute(() => silo1.StopAsync(cancelled.Token));
                Utils.SafeExecute(() => silo2.StopAsync(cancelled.Token));
                Utils.SafeExecute(() => silo1.Dispose());
                Utils.SafeExecute(() => silo2.Dispose());
                Utils.SafeExecute(() => client.Close());
                Utils.SafeExecute(() => client.Dispose());
            }
        }
    }
}
