using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
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
            var silo = new SiloHostBuilder()
                .AddMemoryGrainStorage("MemoryStore")
                .UseLocalhostClustering()
                .Build();

            var client = new ClientBuilder()
                .UseLocalhostClustering()
                .Build();
            try
            {
                await silo.StartAsync();
                await client.Connect();
                var grain = client.GetGrain<IEchoGrain>(Guid.NewGuid());
                var result = await grain.Echo("test");
                Assert.Equal("test", result);
            }
            finally
            {
                await OrleansTaskExtentions.SafeExecute(() => silo.StopAsync());
                await OrleansTaskExtentions.SafeExecute(() => client.Close());
                Utils.SafeExecute(() => silo.Dispose());
                Utils.SafeExecute(() => client.Close());
            }
        }

        /// <summary>
        /// Tests that <see cref="CoreHostingExtensions.UseLocalhostClustering"/> works for multi-silo clusters.
        /// </summary>
        [Fact]
        public async Task LocalhostClusterTest()
        {
            var silo1 = new SiloHostBuilder()
                .AddMemoryGrainStorage("MemoryStore")
                .UseLocalhostClustering(12111, 30001)
                .Build();

            var silo2 = new SiloHostBuilder()
                .AddMemoryGrainStorage("MemoryStore")
                .UseLocalhostClustering(12112, 30002, new IPEndPoint(IPAddress.Loopback, 12111))
                .Build();

            var client = new ClientBuilder()
                .UseLocalhostClustering(30001, 30002)
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
