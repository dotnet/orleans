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
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
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
            var grainToDeactivate = _fixture.Client.GetGrain<IOneWayGrain>(new Guid("00000000-1133-0000-0000-000000000000"));
            var grainId = grainToDeactivate.GetGrainId();
            var topologyView = ((InProcessSiloHandle)_fixture.HostedCluster.Primary!).SiloHost.Services.GetRequiredService<ILocalGrainDirectory>();
            var directorySilo = Assert.IsType<SiloAddress>(topologyView.GetPrimaryForGrain(grainId));
            var nonDirectorySilos = _fixture.HostedCluster.Silos
                .Where(silo => !directorySilo.Equals(silo.SiloAddress))
                .OrderBy(silo => silo.SiloAddress)
                .ToArray();
            Assert.Equal(2, nonDirectorySilos.Length);

            var caller = (InProcessSiloHandle)nonDirectorySilos[0];
            var callerSilo = caller.SiloAddress;
            var targetSilo = nonDirectorySilos[1].SiloAddress;
            var directoryCache = caller.SiloHost.Services.GetRequiredService<TestDirectoryCache>();
            var grainToCallFrom = _fixture.Client.GetGrain<IOneWayGrain>(new Guid("9e773e45-24e0-4e99-b630-6523f5f53b68"));

            RequestContext.Set(IPlacementDirector.PlacementHintKey, callerSilo);
            try
            {
                var grainHost = await grainToCallFrom.GetSiloAddress();
                Assert.Equal(callerSilo, grainHost);
            }
            finally
            {
                RequestContext.Remove(IPlacementDirector.PlacementHintKey);
            }

            // Activate the grain & record its address.
            grainToDeactivate = await grainToCallFrom.GetOtherGrain(grainToDeactivate, targetSilo);
            Assert.Equal(targetSilo, await grainToDeactivate.GetSiloAddress());
            Assert.Equal(directorySilo, await grainToDeactivate.GetPrimaryForGrain());
            var initialActivationId = await grainToDeactivate.GetActivationId();
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
