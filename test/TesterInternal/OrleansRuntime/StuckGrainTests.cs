using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using Orleans.Internal;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using System.Diagnostics;

namespace UnitTests.StuckGrainTests
{
    /// <summary>
    /// Summary description for PersistenceTest
    /// </summary>
    public class StuckGrainTests : OrleansTestingBase, IClassFixture<StuckGrainTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 1;
                builder.AddSiloBuilderConfigurator<SiloHostConfigurator>();
            }

            private class SiloHostConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder.Configure<GrainCollectionOptions>(options =>
                    {
                        options.CollectionAge = TimeSpan.FromSeconds(2);
                        options.CollectionQuantum = TimeSpan.FromSeconds(1);

                        options.DeactivationTimeout = TimeSpan.FromSeconds(2);
                    });

                    hostBuilder.Configure<SiloMessagingOptions>(options =>
                    {
                        options.MaxRequestProcessingTime = TimeSpan.FromSeconds(3);
                    });
                }
            }
        }

        public StuckGrainTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivationCollection")]
        public async Task StuckGrainTest_Basic()
        {
            var id = Guid.NewGuid();
            var stuckGrain = this.fixture.GrainFactory.GetGrain<IStuckGrain>(id);
            var task = stuckGrain.RunForever();

            // Should timeout
            await Assert.ThrowsAsync<TimeoutException>(() => task.WithTimeout(TimeSpan.FromSeconds(1)));

            var cleaner = this.fixture.GrainFactory.GetGrain<IStuckCleanGrain>(id);
            await cleaner.Release(id);

            // Should complete now
            await task.WithTimeout(TimeSpan.FromSeconds(1));

            // wait for activation collection
            await Task.Delay(TimeSpan.FromSeconds(6));

            Assert.False(await cleaner.IsActivated(id), "Grain activation is supposed be garbage collected, but it is still running.");
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivationCollection")]
        public async Task StuckGrainTest_StuckDetectionAndForward()
        {
            var id = Guid.NewGuid();
            var stuckGrain = this.fixture.GrainFactory.GetGrain<IStuckGrain>(id);
            var task = stuckGrain.RunForever();

            // Should timeout
            await Assert.ThrowsAsync<TimeoutException>(() => task.WithTimeout(TimeSpan.FromSeconds(1)));

            for (var i = 0; i < 3; i++)
            {
                await Assert.ThrowsAsync<TimeoutException>(
                    () => stuckGrain.NonBlockingCall().WithTimeout(TimeSpan.FromMilliseconds(500)));
            }

            // Wait so the first task will reach with DefaultCollectionAge timeout
            await Task.Delay(TimeSpan.FromSeconds(3));

            // No issue on this one
            await stuckGrain.NonBlockingCall();

            // All 4 otherwise stuck calls should have been forwarded to a new activation
            Assert.Equal(4, await stuckGrain.GetNonBlockingCallCounter());
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivationCollection")]
        public async Task StuckGrainTest_StuckDetectionOnDeactivation()
        {
            var id = Guid.NewGuid();
            var stuckGrain = this.fixture.GrainFactory.GetGrain<IStuckGrain>(id);
            await stuckGrain.BlockingDeactivation();

            for (var i = 0; i < 3; i++)
            {
                await Assert.ThrowsAsync<TimeoutException>(
                    () => stuckGrain.NonBlockingCall().WithTimeout(TimeSpan.FromMilliseconds(500)));
            }

            // Wait so the first task will reach with DefaultCollectionAge timeout
            await Task.Delay(TimeSpan.FromSeconds(3));

            // No issue on this one
            await stuckGrain.NonBlockingCall();

            // All 4 otherwise stuck calls should have been forwarded to a new activation
            Assert.Equal(4, await stuckGrain.GetNonBlockingCallCounter());
        }
    }
}
