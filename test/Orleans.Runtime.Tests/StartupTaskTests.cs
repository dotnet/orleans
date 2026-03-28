using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.TestingHost;

using TestExtensions;

using UnitTests.GrainInterfaces;

using Xunit;

namespace DefaultCluster.Tests
{
    [TestCategory("BVT"), TestCategory("Lifecycle")]
    public class StartupTaskTests : IClassFixture<StartupTaskTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
                builder.AddSiloBuilderConfigurator<StartupTaskSiloConfigurator>();
            }

            private class StartupTaskSiloConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder.AddStartupTask<CallGrainStartupTask>();
                    hostBuilder.AddStartupTask(
                        async (services, cancellation) =>
                        {
                            var grainFactory = services.GetRequiredService<IGrainFactory>();
                            var grain = grainFactory.GetGrain<ISimpleGrain>(1);
                            await grain.SetA(888);
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
                var grain = this.grainFactory.GetGrain<ISimpleGrain>(2);
                await grain.SetA(777);
            }
        }

        private readonly Fixture fixture;

        public StartupTaskTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        /// <summary>
        /// Ensures that startup tasks can call grains.
        /// </summary>
        [Fact]
        public async Task StartupTaskCanCallGrains()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ISimpleGrain>(1);
            var value = await grain.GetA();
            Assert.Equal(888, value);

            grain = this.fixture.GrainFactory.GetGrain<ISimpleGrain>(2);
            value = await grain.GetA();
            Assert.Equal(777, value);
        }
    }
}
