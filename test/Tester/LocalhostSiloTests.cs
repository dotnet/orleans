using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Internal;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester
{
    /// <summary>
    /// Tests for localhost clustering mode, designed for local development and testing.
    /// 
    /// UseLocalhostClustering configures Orleans to run on localhost without external dependencies
    /// like Azure Storage or SQL Server. This mode:
    /// - Uses in-memory membership provider
    /// - Configures silos to communicate via localhost
    /// - Supports both single-silo and multi-silo configurations
    /// - Ideal for unit tests, development, and debugging
    /// 
    /// These tests verify that localhost clustering works correctly for both single-silo
    /// and multi-silo scenarios, ensuring developers can easily run Orleans locally.
    /// </summary>
    [TestCategory("Functional")]
    public class LocalhostClusterTests
    {
        /// <summary>
        /// Tests localhost clustering with a single silo.
        /// Verifies:
        /// - Silo can start with localhost clustering
        /// - Client can connect using localhost clustering
        /// - Basic grain calls work in this configuration
        /// This is the simplest Orleans setup for local development.
        /// </summary>
        [Fact]
        public async Task LocalhostSiloTest()
        {
            using var portAllocator = new TestClusterPortAllocator();
            var (siloPort, gatewayPort) = portAllocator.AllocateConsecutivePortPairs(1);
            // Configure a single silo with localhost clustering
            // siloPort: for silo-to-silo communication (not used in single silo)
            // gatewayPort: for client-to-silo communication
            var host = new HostBuilder().UseOrleans((ctx, siloBuilder) =>
            {
                siloBuilder.AddMemoryGrainStorage("MemoryStore")
                .UseLocalhostClustering(siloPort, gatewayPort);
            }).Build();

            // Configure client to connect to the localhost silo
            var clientHost = new HostBuilder().UseOrleansClient((ctx, clientBuilder) =>
            {
                clientBuilder.UseLocalhostClustering(gatewayPort);
            }).Build();

            var client = clientHost.Services.GetRequiredService<IClusterClient>();

            try
            {
                await host.StartAsync();
                await clientHost.StartAsync();
                var grain = client.GetGrain<IEchoGrain>(Guid.NewGuid());
                var result = await grain.Echo("test");
                Assert.Equal("test", result);
            }
            finally
            {
                await OrleansTaskExtentions.SafeExecute(() => host.StopAsync());
                await OrleansTaskExtentions.SafeExecute(() => clientHost.StopAsync());
                Utils.SafeExecute(() => host.Dispose());
                Utils.SafeExecute(() => clientHost.Dispose());
            }
        }

        /// <summary>
        /// Tests localhost clustering with multiple silos forming a cluster.
        /// Verifies:
        /// - Multiple silos can form a cluster on localhost
        /// - Silos discover each other through the primary silo endpoint
        /// - Client can connect to multiple gateways for high availability
        /// - Distributed grain directory works in multi-silo setup
        /// This demonstrates a more realistic local development scenario.
        /// </summary>
        [Fact]
        public async Task LocalhostClusterTest()
        {
            using var portAllocator = new TestClusterPortAllocator();
            var (baseSiloPort, baseGatewayPort) = portAllocator.AllocateConsecutivePortPairs(2);
            // Silo 1: Primary silo that others will connect to
            var silo1 = new HostBuilder().UseOrleans((ctx, siloBuilder) =>
            {
                siloBuilder
                .AddMemoryGrainStorage("MemoryStore")
                .UseLocalhostClustering(baseSiloPort, baseGatewayPort);
#pragma warning disable ORLEANSEXP003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                // Distributed grain directory allows grain activations across multiple silos
                siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            }).Build();

            // Silo 2: Secondary silo that connects to the primary
            // Note the third parameter: primary silo endpoint for cluster discovery
            var silo2 = new HostBuilder().UseOrleans((ctx, siloBuilder) =>
            {
                siloBuilder
                .AddMemoryGrainStorage("MemoryStore")
                .UseLocalhostClustering(baseSiloPort + 1, baseGatewayPort + 1, new IPEndPoint(IPAddress.Loopback, baseSiloPort));
#pragma warning disable ORLEANSEXP003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            }).Build();

            // Client configured with multiple gateway ports for load balancing/failover
            var clientHost = new HostBuilder().UseOrleansClient((ctx, clientBuilder) =>
            {
                clientBuilder.UseLocalhostClustering(new[] {baseGatewayPort, baseGatewayPort + 1});
            }).Build();

            var client = clientHost.Services.GetRequiredService<IClusterClient>();

            try
            {
                await Task.WhenAll(silo1.StartAsync(), silo2.StartAsync(), clientHost.StartAsync());

                var grain = client.GetGrain<IEchoGrain>(Guid.NewGuid());
                var result = await grain.Echo("test");
                Assert.Equal("test", result);
            }
            finally
            {
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Utils.SafeExecuteAsync(silo1.StopAsync(cancelled.Token));
                await Utils.SafeExecuteAsync(silo2.StopAsync(cancelled.Token));
                await Utils.SafeExecuteAsync(clientHost.StopAsync(cancelled.Token));
                Utils.SafeExecute(() => silo1.Dispose());
                Utils.SafeExecute(() => silo2.Dispose());
                Utils.SafeExecute(() => clientHost.Dispose());
            }
        }
    }
}
