using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester
{
    public class ClientConnectionEventTests : TestClusterPerTest
    {
        private OutsideRuntimeClient runtimeClient;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddClientBuilderConfigurator<Configurator>();
        }

        public override async Task InitializeAsync()
        {
           await base.InitializeAsync();
           this.runtimeClient = this.HostedCluster.Client.ServiceProvider.GetRequiredService<OutsideRuntimeClient>();
        }

        public class Configurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.Configure<GatewayOptions>(options => options.GatewayListRefreshPeriod = TimeSpan.FromSeconds(1));
            }
        }

        [Fact, TestCategory("SlowBVT")]
        public async Task EventSendWhenDisconnectedFromCluster()
        {
            var runtime = this.HostedCluster.ServiceProvider.GetRequiredService<OutsideRuntimeClient>();

            var semaphore = new SemaphoreSlim(0, 1);
            this.runtimeClient.ClusterConnectionLost += (sender, args) => semaphore.Release();

            // Burst lot of call, to be sure that we are connected to all silos
            for (int i = 0; i < 100; i++)
            {
                var grain = GrainFactory.GetGrain<ITestGrain>(i);
                await grain.SetLabel(i.ToString());
            }

            await this.HostedCluster.StopAllSilosAsync();

            Assert.True(await semaphore.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        [Fact, TestCategory("SlowBVT")]
        public async Task GatewayChangedEventSentOnDisconnectAndReconnect()
        {
            var regainedGatewaySemaphore = new SemaphoreSlim(0, 1);
            var lostGatewaySemaphore = new SemaphoreSlim(0, 1);

            this.runtimeClient.GatewayCountChanged += (sender, args) =>
            {
                if (args.NumberOfConnectedGateways == 1)
                {
                    lostGatewaySemaphore.Release();
                }
                if (args.NumberOfConnectedGateways == 2)
                {
                    regainedGatewaySemaphore.Release();
                }
            };

            var silo = this.HostedCluster.SecondarySilos[0];
            await silo.StopSiloAsync(true);

            Assert.True(await lostGatewaySemaphore.WaitAsync(TimeSpan.FromSeconds(20)));

            await this.HostedCluster.RestartStoppedSecondarySiloAsync(silo.Name);

            // Clients need prodding to reconnect.
            var remainingAttempts = 90;
            bool reconnected;
            do
            {
                this.Client.GetGrain<ITestGrain>(Guid.NewGuid().GetHashCode()).SetLabel("test").Ignore();
                reconnected = await regainedGatewaySemaphore.WaitAsync(TimeSpan.FromSeconds(1));
            } while (!reconnected && --remainingAttempts > 0);

            Assert.True(reconnected, "Failed to reconnect to restarted gateway.");
        }
    }
}
