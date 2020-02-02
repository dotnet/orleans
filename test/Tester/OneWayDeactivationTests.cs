using System;
using System.Threading.Tasks;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.General
{
    [TestCategory("BVT"), TestCategory("OneWay")]
    public class OneWayDeactivationTests : OrleansTestingBase, IClassFixture<OneWayDeactivationTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 3;
            }
        }

        public OneWayDeactivationTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        /// <summary>
        /// Tests that calling [OneWay] methods on an activation which no longer exists triggers a cache invalidation.
        /// Subsequent calls should reactivate the grain.
        /// </summary>
        [Fact]
        public async Task OneWay_Deactivation_CacheInvalidated()
        {
            IOneWayGrain grainToCallFrom;
            while (true)
            {
                grainToCallFrom = this.fixture.Client.GetGrain<IOneWayGrain>(Guid.NewGuid());
                var grainHost = await grainToCallFrom.GetSiloAddress();
                if (grainHost.Equals(this.fixture.HostedCluster.Primary.SiloAddress))
                {
                    break;
                }
            }

            // Activate the grain & record its address.
            var grainToDeactivate = await grainToCallFrom.GetOtherGrain();
            var initialActivationAddress = await grainToCallFrom.GetActivationAddress(grainToDeactivate);

            // Deactivate the grain.
            await grainToDeactivate.Deactivate();

            // We expect cache invalidation to propagate quickly, but will wait a few seconds just in case.
            var remainingAttempts = 50;
            bool cacheUpdated;
            do
            {
                // Have the first grain make a one-way call to the grain which was deactivated.
                // The purpose of this is to trigger a cache invalidation rejection response.
                _ = grainToCallFrom.NotifyOtherGrain();

                // Ask the first grain for its cached value of the second grain's activation address.
                // This value should eventually be updated to a new activation because of the cache invalidation.
                var activationAddress = await grainToCallFrom.GetActivationAddress(grainToDeactivate);

                Assert.True(--remainingAttempts > 0);

                cacheUpdated = !string.Equals(activationAddress, initialActivationAddress);
                if (!cacheUpdated) await Task.Delay(TimeSpan.FromMilliseconds(100));

            } while (!cacheUpdated);
        }
    }
}
