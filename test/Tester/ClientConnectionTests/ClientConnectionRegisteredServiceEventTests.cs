using System;
using System.Collections.Immutable;
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
    public class ClientConnectionRegisteredServiceEventTests : TestClusterPerTest
    {
        private EventNotifier<EventArgs> clusterConnectionLostNotifier;

        private EventNotifier<GatewayCountChangedEventArgs> gatewayCountChangedNotifier;

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            this.clusterConnectionLostNotifier = this.HostedCluster.ServiceProvider.GetRequiredService<EventNotifier<EventArgs>>();
            this.gatewayCountChangedNotifier = this.HostedCluster.ServiceProvider.GetRequiredService<EventNotifier<GatewayCountChangedEventArgs>>();
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddClientBuilderConfigurator<Configurator>();
        }

        public class EventNotifier<TEventArgs>
        {
            private ImmutableList<Action<TEventArgs>> subscribers = ImmutableList<Action<TEventArgs>>.Empty;

            public void Subscribe(Action<TEventArgs> action) => ImmutableInterlocked.Update(ref this.subscribers, (subs, sub) => subs.Add(sub), action);

            public void Unsubscribe(Action<TEventArgs> action) =>
                ImmutableInterlocked.Update(ref this.subscribers, (subs, sub) => subs.Remove(sub), action);

            public void Notify(TEventArgs arg)
            {
                foreach (var sub in this.subscribers)
                {
                    sub(arg);
                }
            }
        }

        public class Configurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                var clusterConnectionLostNotifier = new EventNotifier<EventArgs>();
                var gatewayConnectionCountNotifier = new EventNotifier<GatewayCountChangedEventArgs>();

                clientBuilder.ConfigureServices(s => s.AddSingleton(clusterConnectionLostNotifier));
                clientBuilder.ConfigureServices(s => s.AddSingleton(gatewayConnectionCountNotifier));
                clientBuilder.Configure<GatewayOptions>(options => options.GatewayListRefreshPeriod = TimeSpan.FromSeconds(1));
                clientBuilder.AddClusterConnectionLostHandler((s, e) => clusterConnectionLostNotifier.Notify(e));
                clientBuilder.AddGatewayCountChangedHandler((s, e) => gatewayConnectionCountNotifier.Notify(e));
            }
        }

        [Fact, TestCategory("SlowBVT")]
        public async Task EventSendWhenDisconnectedFromCluster()
        {
            var runtime = this.HostedCluster.ServiceProvider.GetRequiredService<OutsideRuntimeClient>();

            var semaphore = new SemaphoreSlim(0, 1);
            void ReleaseSemaphoreAction(EventArgs args) => semaphore.Release();

            this.clusterConnectionLostNotifier.Subscribe(ReleaseSemaphoreAction);

            try
            {
                // Burst lot of call, to be sure that we are connected to all silos
                for (int i = 0; i < 100; i++)
                {
                    var grain = GrainFactory.GetGrain<ITestGrain>(i);
                    await grain.SetLabel(i.ToString());
                }

                await this.HostedCluster.StopAllSilosAsync();

                Assert.True(await semaphore.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            finally
            {
                this.clusterConnectionLostNotifier.Unsubscribe(ReleaseSemaphoreAction);
            }
        }

        [Fact, TestCategory("SlowBVT")]
        public async Task GatewayChangedEventSentOnDisconnectAndReconnect()
        {
            var regainedGatewaySemaphore = new SemaphoreSlim(0, 1);
            var lostGatewaySemaphore = new SemaphoreSlim(0, 1);

            void ReleaseGatewaySemaphoreAction(GatewayCountChangedEventArgs args)
            {
                if (args.NumberOfConnectedGateways == 1)
                {
                    lostGatewaySemaphore.Release();
                }
                if (args.NumberOfConnectedGateways == 2)
                {
                    regainedGatewaySemaphore.Release();
                }
            }

            this.gatewayCountChangedNotifier.Subscribe(ReleaseGatewaySemaphoreAction);

            try
            {
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
            finally
            {
                this.gatewayCountChangedNotifier.Unsubscribe(ReleaseGatewaySemaphoreAction);
            }
        }
    }
}
