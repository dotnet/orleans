using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.Hosting;
using Orleans.Runtime.GrainDirectory;
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
                builder.AddSiloBuilderConfigurator<SiloConfiguration>();
            }
        }

        public class SiloConfiguration : ISiloConfigurator
        {
            public void Configure(ISiloBuilder siloBuilder) => siloBuilder.ConfigureServices(services =>
            {
                services.Configure<GrainDirectoryOptions>(options =>
                {
                    options.CachingStrategy = GrainDirectoryOptions.CachingStrategyType.Custom;
                });

                services.AddSingleton<TestDirectoryCache>();
                services.AddFromExisting<IGrainDirectoryCache, TestDirectoryCache>();
            });
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
            var caches = fixture.HostedCluster.Silos.Select(s => ((InProcessSiloHandle)s).SiloHost.Services.GetRequiredService<TestDirectoryCache>()).ToList();
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

            var grainId = grainToDeactivate.GetGrainId();
            var initialActivationId = await grainToDeactivate.GetActivationId();
            await grainToDeactivate.Deactivate();
            await grainToCallFrom.SignalSelfViaOther();
            var (count, finalActivationId) = await grainToCallFrom.WaitForSignal();
            Assert.Equal(1, count);
            Assert.NotEqual(initialActivationId, finalActivationId);

            // Test that cache was invalidated.
            var found = false;
            foreach (var cache in caches)
            {
                foreach (var op in cache.Operations)
                {
                    if (op is not TestDirectoryCache.CacheOperation.RemoveActivation removeActivation)
                    {
                        continue;
                    }

                    // We don't know what the whole activation address should be, but we do know
                    // that some entry should be successfully removed for the provided grain id.
                    if (removeActivation.Key.Grain.Equals(grainId) && removeActivation.Result)
                    {
                        found = true;
                        break;
                    }
                }
            }

            Assert.True(found, "Should have processed a cache invalidation for the target activation");
            foreach (var cache in caches)
            {
                cache.Operations.Clear();
            }
        }
    }
}
