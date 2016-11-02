using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
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
        private class Fixture : BaseClusterFixture
        {
            protected override TestingSiloHost CreateClusterHost()
            {
                return new TestingSiloHost(new TestingSiloOptions
                {
                    StartSecondary = false,
                    AdjustConfig = config =>
                    {
                        GlobalConfiguration.ENFORCE_MINIMUM_REQUIREMENT_FOR_AGE_LIMIT = false;
                        config.Globals.Application.SetDefaultCollectionAgeLimit(TimeSpan.FromSeconds(1));
                        config.Globals.CollectionQuantum = TimeSpan.FromSeconds(1);
                    }
                });
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivationCollection")]
        public async Task StuckGrainTest_Basic()
        {
            var id = Guid.NewGuid();
            var stuckGrain = GrainClient.GrainFactory.GetGrain<IStuckGrain>(id);
            var task = stuckGrain.RunForever();

            var timeoutTask = task.WithTimeout(TimeSpan.FromSeconds(1));

            bool excThrown = false;

            try
            {
                await timeoutTask;
            }
            catch (TimeoutException)
            {
                excThrown = true;
            }

            Assert.True(excThrown, "Timeout exceptions hasn't been thrown for call that is supposed to run forever.");

            var cleaner = GrainClient.GrainFactory.GetGrain<IStuckCleanGrain>(id);
            await cleaner.Release(id);

            timeoutTask = task.WithTimeout(TimeSpan.FromSeconds(1));

            excThrown = false;

            try
            {
                await timeoutTask;
            }
            catch (TimeoutException)
            {
                excThrown = true;
            }

            Assert.False(excThrown, "Timeout exceptions has been thrown for call that is supposed to complete.");

            await Task.Delay(TimeSpan.FromSeconds(2)); // wait for activation collection

            var activated = await cleaner.IsActivated(id);

            Assert.False(activated, "Grain activation is supposed be garbage collected, but it is still running.");
        }
    }
}
