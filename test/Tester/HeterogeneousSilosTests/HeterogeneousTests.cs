using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.Utilities;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace Tester.HeterogeneousSilosTests
{
    [TestCategory("Functional")]
    public class HeterogeneousTests : OrleansTestingBase, IDisposable, IAsyncLifetime
    {
        private static readonly TimeSpan ClientRefreshDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(200);
        private TestCluster cluster;

        private void SetupAndDeployCluster(Type defaultPlacementStrategy, params Type[] blackListedTypes)
        {
            cluster?.StopAllSilos();
            var builder = new TestClusterBuilder(1)
            {
                CreateSiloAsync = AppDomainSiloHandle.Create
            };
            builder.Properties["DefaultPlacementStrategy"] = RuntimeTypeNameFormatter.Format(defaultPlacementStrategy);
            builder.Properties["BlacklistedGrainTypes"] = string.Join("|", blackListedTypes.Select(t => t.FullName));
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            builder.AddClientBuilderConfigurator<ClientConfigurator>();
            cluster = builder.Build();
            cluster.Deploy();
        }

        public class SiloConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.Configure<SiloMessagingOptions>(options => options.AssumeHomogenousSilosForTesting = false);
                hostBuilder.Configure<TypeManagementOptions>(options => options.TypeMapRefreshInterval = RefreshInterval);
                hostBuilder.Configure<GrainClassOptions>(options =>
                {
                    var cfg = hostBuilder.GetConfiguration();
                    var siloOptions = new TestSiloSpecificOptions();
                    cfg.Bind(siloOptions);

                    // The blacklist is only intended for the primary silo in these tests.
                    if (string.Equals(siloOptions.SiloName, Silo.PrimarySiloName))
                    {
                        var blacklistedTypesList = cfg["BlacklistedGrainTypes"].Split('|').ToList();
                        options.ExcludedGrainTypes.AddRange(blacklistedTypesList);
                    }
                });
                hostBuilder.ConfigureServices(services =>
                {
                    var defaultPlacementStrategy = Type.GetType(hostBuilder.GetConfiguration()["DefaultPlacementStrategy"]);
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
            cluster?.StopAllSilos();
            cluster = null;
        }

        [Fact]
        public void GrainExcludedTest()
        {
            SetupAndDeployCluster(typeof(RandomPlacement), typeof(TestGrain));

            // Should fail
            var exception = Assert.Throws<ArgumentException>(() => this.cluster.GrainFactory.GetGrain<ITestGrain>(0));
            Assert.Contains("Cannot find an implementation class for grain interface", exception.Message);

            // Should not fail
            this.cluster.GrainFactory.GetGrain<ISimpleGrainWithAsyncMethods>(0);
        }


        [Fact]
        public async Task MergeGrainResolverTests()
        {
            await MergeGrainResolverTestsImpl(typeof(RandomPlacement), true, typeof(TestGrain));
            await MergeGrainResolverTestsImpl(typeof(PreferLocalPlacement), true, typeof(TestGrain));
            // TODO Check ActivationCountBasedPlacement in tests
            //await MergeGrainResolverTestsImpl("ActivationCountBasedPlacement", typeof(TestGrain));
        }

        [Fact]
        public async Task MergeGrainResolverWithClientRefreshTests()
        {
            await MergeGrainResolverTestsImpl(typeof(RandomPlacement), false, typeof(TestGrain));
            await MergeGrainResolverTestsImpl(typeof(PreferLocalPlacement), false, typeof(TestGrain));
            // TODO Check ActivationCountBasedPlacement in tests
            //await MergeGrainResolverTestsImpl("ActivationCountBasedPlacement", typeof(TestGrain));
        }

        private async Task MergeGrainResolverTestsImpl(Type defaultPlacementStrategy, bool restartClient, params Type[] blackListedTypes)
        {
            SetupAndDeployCluster(defaultPlacementStrategy, blackListedTypes);

            var delayTimeout = RefreshInterval.Add(RefreshInterval);

            // Should fail
            var exception = Assert.Throws<ArgumentException>(() => this.cluster.GrainFactory.GetGrain<ITestGrain>(0));
            Assert.Contains("Cannot find an implementation class for grain interface", exception.Message);

            // Start a new silo with TestGrain
            await cluster.StartAdditionalSiloAsync();
            await Task.Delay(delayTimeout);

            if (restartClient)
            {
                // Disconnect/Reconnect the client
                await cluster.Client.Close();
                cluster.Client.Dispose();
                cluster.InitializeClient();
            }
            else
            {
                await Task.Delay(ClientRefreshDelay.Multiply(3));
            }

            for (var i = 0; i < 5; i++)
            {
                // Success
                var g = this.cluster.GrainFactory.GetGrain<ITestGrain>(i);
                await g.SetLabel("Hello world");
            }

            // Stop the latest silos
            await cluster.StopSecondarySilosAsync();
            await Task.Delay(delayTimeout);

            if (restartClient)
            {
                // Disconnect/Reconnect the client
                await cluster.Client.Close();
                cluster.Client.Dispose();
                cluster.InitializeClient();
            }
            else
            {
                await Task.Delay(ClientRefreshDelay.Multiply(3));
            }

            // Should fail
            exception = Assert.Throws<ArgumentException>(() => this.cluster.GrainFactory.GetGrain<ITestGrain>(0));
            Assert.Contains("Cannot find an implementation class for grain interface", exception.Message);
        }

        public Task InitializeAsync()
        {
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            if (this.cluster == null) return;
            await cluster.StopAllSilosAsync();
        }
    }
}
