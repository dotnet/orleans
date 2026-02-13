using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.TestHelper;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.MembershipTests
{
    /// <summary>
    /// Tests for client directory partition reconstruction after silo failures.
    /// </summary>
    public class ClientIdPartitionDataRebuildTests : IDisposable
    {
        private MembershipDiagnosticObserver _membershipObserver;
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
            _membershipObserver = MembershipDiagnosticObserver.Create();
        }

        [Fact]
        //[SkippableFact(typeof(SiloUnavailableException)), TestCategory("Functional")]
        public async Task ReconstructClientIdPartitionTest_Observer()
        {
            // Ensure the client entry is on Silo2 partition and get a grain that live on Silo3
            var grain = await SetupTestAndPickGrain<ISimpleObserverableGrain>(g => g.GetRuntimeInstanceId());
            var observer = new Observer();
            var reference = this.hostedCluster.GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer);

            await grain.Subscribe(reference);

            // Test first notification
            await grain.SetA(10);
            await observer.WaitForNotification(10, 0, TimeSpan.FromSeconds(10));

            // Remember the silo address we're about to kill
            var siloToKill = this.hostedCluster.SecondarySilos[0].SiloAddress;
            _membershipObserver.Clear();

            // Kill the silo that holds directory client entry
            await this.hostedCluster.SecondarySilos[0].StopSiloAsync(stopGracefully: false);

            // Wait for the cluster to detect the dead silo using diagnostic events
            // instead of arbitrary Task.Delay(5000)
            await _membershipObserver.WaitForSiloStatusAsync(siloToKill, "Dead", TimeSpan.FromSeconds(30));

            // Second notification should work since the directory was "rebuilt" when
            // silos in cluster detected the dead one
            await grain.SetB(20);
            await observer.WaitForNotification(10, 20, TimeSpan.FromSeconds(10));
        }

        [Fact]
        //[SkippableFact(typeof(SiloUnavailableException)), TestCategory("Functional")]
        public async Task ReconstructClientIdPartitionTest_Request()
        {
            // Ensure the client entry is on Silo2 partition and get a grain that lives on Silo3
            var grain = await SetupTestAndPickGrain<ITestGrain>(g => g.GetRuntimeInstanceId());

            // Verify we can call the grain before killing the silo
            var labelBefore = await grain.GetLabel();

            // Remember the silo address we're about to kill
            var siloToKill = this.hostedCluster.SecondarySilos[0].SiloAddress;
            _membershipObserver.Clear();

            // Kill the silo that holds directory client entry (Silo2)
            // The grain is on Silo3, so it should still be accessible
            await this.hostedCluster.SecondarySilos[0].StopSiloAsync(stopGracefully: false);

            // Wait for the cluster to detect the dead silo using diagnostic events
            await _membershipObserver.WaitForSiloStatusAsync(siloToKill, "Dead", TimeSpan.FromSeconds(30));

            // The grain should still be accessible since the directory was "rebuilt" when
            // silos in cluster detected the dead one. The grain is on Silo3, only the
            // directory partition owner (Silo2) was killed.
            await grain.SetLabel("AfterSiloDeath");
            var labelAfter = await grain.GetLabel();
            Assert.Equal("AfterSiloDeath", labelAfter);
        }

        private async Task<T> SetupTestAndPickGrain<T>(Func<T, Task<string>> getRuntimeInstanceId) where T : class, IGrainWithIntegerKey
        {
            // Ensure the client entry is on Silo2 partition
            GrainId clientId = default;
            CreateAndDeployTestCluster();
            for (var i = 0; i < 100; i++)
            {
                if (this.hostedCluster.Client == null)
                {
                    await this.hostedCluster.InitializeClientAsync();
                }

                var client = this.hostedCluster.ServiceProvider.GetRequiredService<OutsideRuntimeClient>();
                clientId = client.CurrentActivationAddress.GrainId;
                var report = await TestUtils.GetDetailedGrainReport(this.hostedCluster.InternalGrainFactory, clientId, hostedCluster.Primary);
                if (this.hostedCluster.SecondarySilos[0].SiloAddress.Equals(report.PrimaryForGrain))
                {
                    break;
                }
                clientId = default;
                await this.hostedCluster.KillClientAsync();
            }
            Assert.False(clientId.IsDefault);

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

            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            builder.AddClientBuilderConfigurator<ClientConfigurator>();
            this.hostedCluster = builder.Build();
            this.hostedCluster.Deploy();
        }

        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.Configure<ClusterMembershipOptions>(options =>
                {
                    options.NumMissedProbesLimit = 1;
                    options.ProbeTimeout = TimeSpan.FromMilliseconds(500);
                    options.NumVotesForDeathDeclaration = 1;
                });

                // Disable grain directory caching to force directory lookups
                hostBuilder.Configure<GrainDirectoryOptions>(options =>
                    options.CachingStrategy = GrainDirectoryOptions.CachingStrategyType.None);
            }
        }

        public class ClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.Configure<GatewayOptions>(options => options.PreferredGatewayIndex = 0);
                // Set a longer response timeout to allow for directory reconstruction after silo death
                clientBuilder.Configure<ClientMessagingOptions>(options => options.ResponseTimeout = TimeSpan.FromSeconds(30));
            }
        }

        public void Dispose()
        {
            try
            {
                hostedCluster?.StopAllSilos();
            }
            finally
            {
                hostedCluster?.Dispose();
                hostedCluster = null;
                _membershipObserver?.Dispose();
                _membershipObserver = null;
            }
        }
    }
}
