using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.TestHelper;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.MembershipTests
{
    public class ClientIdPartitionDataRebuildTests : IDisposable
    {
        internal class Observer : ISimpleGrainObserver
        {
            private readonly SemaphoreSlim semaphore = new SemaphoreSlim(0);
            private int lastA;
            private int lastB;

            public void StateChanged(int a, int b)
            {
                this.lastA = a;
                this.lastB = b;
                this.semaphore.Release();
            }

            public async Task WaitForNotification(int expectedA, int expectedB, TimeSpan timeout)
            {
                Assert.True(await this.semaphore.WaitAsync(timeout), "No notification received");
                Assert.Equal(expectedA, this.lastA);
                Assert.Equal(expectedB, this.lastB);
            }
        }

        private readonly ITestOutputHelper output;

        private TestCluster hostedCluster;

        public ClientIdPartitionDataRebuildTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact(Skip = "Not reliable in PR build, skipping for now")]
        //[SkippableFact(typeof(SiloUnavailableException)), TestCategory("Functional")]
        public async Task ReconstructClientIdPartitionTest_Observer()
        {
            // Ensure the client entry is on Silo2 partition and get a grain that live on Silo3
            var grain = await SetupTestAndPickGrain<ISimpleObserverableGrain>(g => g.GetRuntimeInstanceId());
            var observer = new Observer();
            var reference = await this.hostedCluster.GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer);

            await grain.Subscribe(reference);

            // Test first notification
            await grain.SetA(10);
            await observer.WaitForNotification(10, 0, TimeSpan.FromSeconds(10));

            // Kill the silo that hold directory client entry
            this.hostedCluster.SecondarySilos[0].StopSilo(stopGracefully: false);
            await Task.Delay(5000);

            // Second notification should work since the directory was "rebuilt" when
            // silos in cluster detected the dead one
            await grain.SetB(20);
            await observer.WaitForNotification(10, 20, TimeSpan.FromSeconds(10));
        }

        [Fact(Skip = "Not reliable in PR build, skipping for now")]
        //[SkippableFact(typeof(SiloUnavailableException)), TestCategory("Functional")]
        public async Task ReconstructClientIdPartitionTest_Request()
        {
            // Ensure the client entry is on Silo2 partition and get a grain that live on Silo2
            var grain = await SetupTestAndPickGrain<ITestGrain>(g => g.GetRuntimeInstanceId());

            // Launch a long task and kill the silo that hold directory client entry
            var promise = grain.DoLongAction(TimeSpan.FromSeconds(10), "LongAction");
            this.hostedCluster.SecondarySilos[0].StopSilo(stopGracefully: false);

            // It should work since the directory was "rebuilt" when
            // silos in cluster detected the dead one
            await promise;
        }

        private async Task<T> SetupTestAndPickGrain<T>(Func<T, Task<string>> getRuntimeInstanceId) where T : class, IGrainWithIntegerKey
        {
            // Ensure the client entry is on Silo2 partition
            GrainId clientId = null;
            CreateAndDeployTestCluster();
            for (var i = 0; i < 100; i++)
            {
                if (this.hostedCluster.Client == null)
                {
                    this.hostedCluster.InitializeClient();
                }

                var client = this.hostedCluster.ServiceProvider.GetRequiredService<OutsideRuntimeClient>();
                clientId = client.CurrentActivationAddress.Grain;
                var report = await TestUtils.GetDetailedGrainReport(this.hostedCluster.InternalGrainFactory, clientId, hostedCluster.Primary);
                if (this.hostedCluster.SecondarySilos[0].SiloAddress.Equals(report.PrimaryForGrain))
                {
                    break;
                }
                clientId = null;
                this.hostedCluster.KillClient();
            }
            Assert.NotNull(clientId);

            // Ensure grain is activated on Silo3
            T grain = null;
            for (var i = 0; i < 100; i++)
            {
                grain = this.hostedCluster.GrainFactory.GetGrain<T>(i);
                var instanceId = await getRuntimeInstanceId(grain);
                if (instanceId.Contains(hostedCluster.SecondarySilos[1].SiloAddress.Endpoint.ToString()))
                {
                    break;
                }
                grain = null;
            }
            Assert.NotNull(grain);

            return grain;
        }

        private void CreateAndDeployTestCluster()
        {
            var builder = new TestClusterBuilder(3);

            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClusterConfiguration.Globals.NumMissedProbesLimit = 1;
                legacy.ClusterConfiguration.Globals.ProbeTimeout = TimeSpan.FromMilliseconds(500);
                legacy.ClusterConfiguration.Globals.NumVotesForDeathDeclaration = 1;
                legacy.ClusterConfiguration.Globals.CacheSize = 0;

                // use only Primary as the gateway
                legacy.ClientConfiguration.Gateways = legacy.ClientConfiguration.Gateways.Take(1).ToList();
            });
            this.hostedCluster = builder.Build();
            this.hostedCluster.Deploy();
        }

        public void Dispose()
        {
            this.hostedCluster?.StopAllSilos();
        }
    }
}
