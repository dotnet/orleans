using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.General
{
    [TestCategory("BVT"), TestCategory("OneWay")]
    public class OneWayDeactivationTests : OrleansTestingBase, IClassFixture<OneWayDeactivationTests.Fixture>
    {
        private readonly Fixture _fixture;

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
            _fixture = fixture;
        }

        /// <summary>
        /// Tests that calling [OneWay] methods on an activation which no longer exists triggers a cache invalidation.
        /// Subsequent calls should reactivate the grain.
        /// </summary>
        [Fact]
        public async Task OneWay_Deactivation_CacheInvalidated()
        {
            var directoryCache = ((InProcessSiloHandle)_fixture.HostedCluster.Primary).SiloHost.Services.GetRequiredService<TestDirectoryCache>();
            IOneWayGrain grainToCallFrom;
            while (true)
            {
                RequestContext.Set(IPlacementDirector.PlacementHintKey, _fixture.HostedCluster.Primary.SiloAddress);
                grainToCallFrom = _fixture.Client.GetGrain<IOneWayGrain>(Guid.NewGuid());
                var grainHost = await grainToCallFrom.GetSiloAddress();
                if (grainHost.Equals(_fixture.HostedCluster.Primary.SiloAddress))
                {
                    break;
                }
            }

            // Activate the grain & record its address.
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
            var grainToDeactivate = await grainToCallFrom.GetOtherGrain();
            var initialActivationId = await grainToDeactivate.GetActivationId();
            var grainId = grainToDeactivate.GetGrainId();
            var activationAddress = directoryCache.Operations
                .OfType<TestDirectoryCache.CacheOperation.AddOrUpdate>()
                .Last(op => op.Value.GrainId.Equals(grainId))
                .Value;
            await grainToDeactivate.Deactivate();
            await grainToCallFrom.SignalSelfViaOther();
            var (count, finalActivationId) = await grainToCallFrom.WaitForSignal();
            Assert.Equal(1, count);
            Assert.NotEqual(initialActivationId, finalActivationId);

            // Test that cache was updated.
            // We don't know what the whole activation address should be, but we do know
            // that some entry should be successfully updated for the provided grain id.
            var newActivationAddress = directoryCache.Operations
                .OfType<TestDirectoryCache.CacheOperation.AddOrUpdate>()
                .Last(op => op.Value.GrainId.Equals(grainId))
                .Value;
            Assert.NotNull(newActivationAddress);

            directoryCache.Operations.Clear();
        }
    }
}
