using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace Tester.HeterogeneousSilosTests
{
    /// <summary>
    /// Tests for heterogeneous silo configurations including grain type exclusion and type resolution merging.
    /// </summary>
    [TestSuite("Functional")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    [TestCategory("Functional")]
    public class HeterogeneousTests : OrleansTestingBase, IDisposable, IAsyncLifetime
    {
        private static readonly TimeSpan ClientRefreshDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(200);
        private TestCluster? cluster;

        private void SetupAndDeployCluster(Type defaultPlacementStrategy, params Type[] blackListedTypes)
        {
            cluster?.StopAllSilos();
            var builder = new TestClusterBuilder(1);
            builder.Properties["DefaultPlacementStrategy"] = RuntimeTypeNameFormatter.Format(defaultPlacementStrategy);
            builder.Properties["BlockedGrainTypes"] = string.Join("|", blackListedTypes.Select(t => RuntimeTypeNameFormatter.Format(t)));
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            builder.AddClientBuilderConfigurator<ClientConfigurator>();
            cluster = builder.Build();
            cluster.Deploy();
        }

        public class SiloConfigurator : IHostConfigurator
        {
            public void Configure(IHostBuilder hostBuilder)
            {
                hostBuilder.ConfigureServices(services =>
                {
                    services.Configure<SiloMessagingOptions>(options => options.AssumeHomogenousSilosForTesting = false);
                    services.Configure<TypeManagementOptions>(options => options.TypeMapRefreshInterval = RefreshInterval);
                    services.AddOptions<GrainTypeOptions>().Configure((GrainTypeOptions options, IOptions<SiloOptions> siloOptions) =>
                    {
                        var cfg = hostBuilder.GetConfiguration();

                        // The blocklist is only intended for the primary silo in these tests.
                        if (string.Equals(siloOptions.Value.SiloName, Silo.PrimarySiloName, StringComparison.Ordinal))
                        {
                            var typeNames = cfg["BlockedGrainTypes"]!.Split('|').ToList();
                            foreach (var typeName in typeNames)
                            {
                                var type = Type.GetType(typeName)!;
                                options.Classes.Remove(type);
                            }
                        }
                    });

                    var defaultPlacementStrategy = Type.GetType(hostBuilder.GetConfiguration()["DefaultPlacementStrategy"]!)!;
                    services.AddSingleton(typeof(PlacementStrategy), defaultPlacementStrategy);
                });
            }
        }

        public class ClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.Configure<TypeManagementOptions>(options => options.TypeMapRefreshInterval = ClientRefreshDelay);
            }
        }

        public void Dispose()
        {
            cluster?.Dispose();
            cluster = null;
        }

        [Fact]
        public void GrainExcludedTest()
        {
            SetupAndDeployCluster(typeof(RandomPlacement), typeof(TestGrain));

            // Should fail
            var exception = Assert.Throws<ArgumentException>(() => this.cluster!.GrainFactory!.GetGrain<ITestGrain>(0));
            Assert.Contains("Could not find an implementation for interface", exception.Message);

            // Should not fail
            this.cluster!.GrainFactory!.GetGrain<ISimpleGrainWithAsyncMethods>(0);
        }


        [Fact]
        public async Task MergeGrainResolverTests()
        {
            await MergeGrainResolverTestsImpl<ITestGrain>(typeof(RandomPlacement), true, this.CallITestGrainMethod, typeof(TestGrain));
            await MergeGrainResolverTestsImpl<ITestGrain>(typeof(PreferLocalPlacement), true, this.CallITestGrainMethod, typeof(TestGrain));
            // TODO Check ActivationCountBasedPlacement in tests
            //await MergeGrainResolverTestsImpl("ActivationCountBasedPlacement", typeof(TestGrain));
        }

        [Fact]
        public async Task MergeGrainResolverWithClientRefreshTests()
        {
            await MergeGrainResolverTestsImpl<ITestGrain>(typeof(RandomPlacement), false, this.CallITestGrainMethod, typeof(TestGrain));
            await MergeGrainResolverTestsImpl<ITestGrain>(typeof(PreferLocalPlacement), false, this.CallITestGrainMethod, typeof(TestGrain));
            // TODO Check ActivationCountBasedPlacement in tests
            //await MergeGrainResolverTestsImpl("ActivationCountBasedPlacement", typeof(TestGrain));
        }

        [Fact]
        public async Task StatelessWorkerPlacementTests()
        {
            await MergeGrainResolverTestsImpl<IStatelessWorkerGrain>(typeof(RandomPlacement), true, this.CallIStatelessWorkerGrainMethod, typeof(StatelessWorkerGrain));
            await MergeGrainResolverTestsImpl<IStatelessWorkerGrain>(typeof(PreferLocalPlacement), true, this.CallIStatelessWorkerGrainMethod, typeof(StatelessWorkerGrain));
        }

        [Fact]
        public async Task StatelessWorkerPlacementWithClientRefreshTests()
        {
            await MergeGrainResolverTestsImpl<IStatelessWorkerGrain>(typeof(RandomPlacement), false, this.CallIStatelessWorkerGrainMethod, typeof(StatelessWorkerGrain));
            await MergeGrainResolverTestsImpl<IStatelessWorkerGrain>(typeof(PreferLocalPlacement), false, this.CallIStatelessWorkerGrainMethod, typeof(StatelessWorkerGrain));
        }

        private async Task CallITestGrainMethod(IGrain grain)
        {
            var g = grain.Cast<ITestGrain>();
            await g.SetLabel("Hello world");
        }

        private async Task CallIStatelessWorkerGrainMethod(IGrain grain)
        {
            var g = grain.Cast<IStatelessWorkerGrain>();
            await g.GetCallStats();
        }

        private async Task WaitForClusterStateToStabilizeAsync(bool restartClient)
        {
            await cluster!.WaitForLivenessToStabilizeAsync();
            await cluster.WaitForClusterManifestToStabilizeAsync();

            if (restartClient)
            {
                await cluster.StopClusterClientAsync();
                await cluster.InitializeClientAsync();
            }

            var expectedSilos = cluster.GetActiveSilos().Select(static silo => silo.SiloAddress).ToHashSet();
            var manifestProvider = cluster.ServiceProvider.GetRequiredService<IClusterManifestProvider>();
            var lastObservedSilos = Array.Empty<SiloAddress>();
            using var cancellation = new CancellationTokenSource(TestConstants.InitTimeout);
            try
            {
                await foreach (var manifest in manifestProvider.Updates.WithCancellation(cancellation.Token))
                {
                    lastObservedSilos = manifest.Silos.Keys.Where(static silo => !silo.IsClient).ToArray();
                    if (expectedSilos.SetEquals(lastObservedSilos))
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The client cluster manifest did not converge. Expected silos: {string.Join(", ", expectedSilos.Select(static silo => silo.ToString()))}. "
                    + $"Last observed silos: {string.Join(", ", lastObservedSilos.Select(static silo => silo.ToString()))}.");
            }

            throw new InvalidOperationException("The client cluster manifest update stream completed before the expected cluster state was observed.");
        }

        private async Task MergeGrainResolverTestsImpl<T>(Type defaultPlacementStrategy, bool restartClient, Func<IGrain, Task> func, params Type[] blackListedTypes)
            where T : IGrainWithIntegerKey
        {
            SetupAndDeployCluster(defaultPlacementStrategy, blackListedTypes);

            // Should fail
            var exception = Assert.Throws<ArgumentException>(() => this.cluster!.GrainFactory!.GetGrain<T>(0));
            Assert.Contains("Could not find an implementation for interface", exception.Message);

            // Start a new silo with TestGrain
            await cluster!.StartAdditionalSiloAsync();
            await WaitForClusterStateToStabilizeAsync(restartClient);

            for (var i = 0; i < 5; i++)
            {
                // Success
                var g = this.cluster.GrainFactory!.GetGrain<T>(i);
                await func(g);
            }

            // Stop the latest silos
            await cluster.StopSecondarySilosAsync();
            await WaitForClusterStateToStabilizeAsync(restartClient);

            // Should fail
            exception = Assert.Throws<ArgumentException>(() => this.cluster.GrainFactory!.GetGrain<T>(0));
            Assert.Contains("Could not find an implementation for interface", exception.Message);
        }

        public ValueTask InitializeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (this.cluster is TestCluster c)
                {
                    await c.StopAllSilosAsync();
                }
            }
            finally
            {
                this.cluster?.Dispose();
            }
        }
    }
}
