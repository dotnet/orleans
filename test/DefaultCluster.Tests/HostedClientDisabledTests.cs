using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using Tester.CodeGenTests;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    [TestCategory("BVT"), TestCategory("HostedClient")]
    public class HostedClientDisabledTests : IClassFixture<HostedClientDisabledTests.Fixture>
    {
        private readonly ISiloHost silo;

        public class Fixture : IDisposable
        {
            public ISiloHost Silo { get; }

            public Fixture()
            {
                var (siloPort, gatewayPort) = TestClusterNetworkHelper.GetRandomAvailableServerPorts();
                this.Silo = new SiloHostBuilder()
                    .UseLocalhostClustering(siloPort, gatewayPort)
                    .Configure<ClusterOptions>(options =>
                    {
                        options.ClusterId = Guid.NewGuid().ToString();
                        options.ServiceId = Guid.NewGuid().ToString();
                    })
                    .AddMemoryGrainStorage("PubSubStore")
                    .AddMemoryStreams<DefaultMemoryMessageBodySerializer>("MemStream")
                    .Build();
                this.Silo.StartAsync().GetAwaiter().GetResult();
            }

            public void Dispose()
            {
                this.Silo?.Dispose();
            }
        }

        public HostedClientDisabledTests(Fixture fixture)
        {
            this.silo = fixture.Silo;
        }

        [Fact]
        public void HostedClient_Disabled_IClusterClient()
        {
            var client = this.silo.Services.GetService<IClusterClient>();
            Assert.Null(client);
        }

        [Fact]
        public async Task HostedClient_Disabled_GrainCallTest()
        {
            // IGrainFactory will always be present in the container, since it's used by the runtime itself.
            var client = this.silo.Services.GetRequiredService<IGrainFactory>();
            var grain = client.GetGrain<IGrainWithGenericMethods>(Guid.NewGuid());

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.RoundTrip("hi"));
            Assert.Contains("not enabled", exception.Message);
        }

        [Fact]
        public async Task HostedClient_Disabled_ObserverTest()
        {
            var client = this.silo.Services.GetRequiredService<IGrainFactory>();

            var observer = new ObserverTests.SimpleGrainObserver((i, i1, arg3) => { }, new AsyncResultHandle(), new NullLogger<HostedClientDisabledTests>());
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateObjectReference<ISimpleGrainObserver>(observer));
            Assert.Contains("not enabled", exception.Message);
        }

        [Fact]
        public void HostedClient_Disabled_StreamTest()
        {
            var streamProvider = this.silo.Services.GetRequiredServiceByName<IStreamProvider>("MemStream");
            var exception = Assert.Throws<InvalidOperationException>(() => streamProvider.GetStream<int>(Guid.Empty, "hi"));
            Assert.Contains("not enabled", exception.Message);
        }
    }
}