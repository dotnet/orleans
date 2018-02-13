using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;

using TestExtensions;

using UnitTests.GrainInterfaces;

using Xunit;

namespace DefaultCluster.Tests
{
    [TestCategory("BVT")]
    public class StartupTaskTests : IClassFixture<StartupTaskTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
                builder.AddSiloBuilderConfigurator<StartupTaskSiloConfigurator>();
            }

            private class StartupTaskSiloConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder.AddStartupTask<CallGrainStartupTask>();
                    hostBuilder.AddStartupTask(
                        async (services, cancellation) =>
                        {
                            var grainFactory = services.GetRequiredService<IGrainFactory>();
                            var grain = grainFactory.GetGrain<ISimpleGrain>(98052);

                            // Modify "A" to validate ordering of events.
                            await grain.SetA(await grain.GetA() * 2);
                        });
                }
            }
        }

        public class CallGrainStartupTask : IStartupTask
        {
            private readonly IGrainFactory grainFactory;

            public CallGrainStartupTask(IGrainFactory grainFactory)
            {
                this.grainFactory = grainFactory;
            }

            public async Task Execute(CancellationToken cancellationToken)
            {
                var grain = this.grainFactory.GetGrain<ISimpleGrain>(98052);
                await grain.SetA(1331);
            }
        }

        private readonly Fixture fixture;

        public StartupTaskTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        /// <summary>
        /// Ensures that startup tasks can call grains and are executed in the registered order.
        /// </summary>
        [Fact]
        public async Task StartupTaskCanCallGrains()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ISimpleGrain>(98052);
            var value = await grain.GetA();
            Assert.Equal(2662, value);
        }
    }
}
