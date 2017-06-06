using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

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
            protected override TestCluster CreateTestCluster()
            {
                GlobalConfiguration.ENFORCE_MINIMUM_REQUIREMENT_FOR_AGE_LIMIT = false;
                var options = new TestClusterOptions(1);
                options.ClusterConfiguration.Globals.Application.SetDefaultCollectionAgeLimit(TimeSpan.FromSeconds(3));
                options.ClusterConfiguration.Globals.MaxRequestProcessingTime = TimeSpan.FromSeconds(3);
                options.ClusterConfiguration.Globals.CollectionQuantum = TimeSpan.FromSeconds(1);

                return new TestCluster(options);
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

            Assert.Equal(1, await stuckGrain.GetNonBlockingCallCounter());
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

            Assert.Equal(1, await stuckGrain.GetNonBlockingCallCounter());
        }
    }
}
