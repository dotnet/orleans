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
using Orleans.Runtime;
using UnitTests.Grains;

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

                        options.ActivationTimeout = TimeSpan.FromSeconds(2);
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

            await StuckGrain.WaitForDeactivationStart(stuckGrain.GetGrainId());

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
        public async Task StuckGrainTest_StuckDetectionOnActivation()
        {
            var id = Guid.NewGuid();
            var stuckGrain = this.fixture.GrainFactory.GetGrain<IStuckGrain>(id);

            // The cancellation token passed to OnActivateAsync should become cancelled and this will cause activation to fail.
            RequestContext.Set("block_activation_seconds", 30);
            await Assert.ThrowsAsync<TaskCanceledException>(() => stuckGrain.NonBlockingCall());

            // Check to see that it did try to activate.
            RequestContext.Clear();
            var unstuckGrain = this.fixture.GrainFactory.GetGrain<IStuckGrain>(Guid.NewGuid());
            await unstuckGrain.NonBlockingCall();

            var activationAttempted = await unstuckGrain.DidActivationTryToStart(stuckGrain.GetGrainId());
            Assert.True(activationAttempted);

            // Now that activation is not blocked (we cleared the request context value which told it to block), let's check that our previously stuck grain works.
            await stuckGrain.NonBlockingCall();
        }
    }
}
