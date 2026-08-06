using System.Collections.Generic;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Xunit;

namespace UnitTests.Runtime
{
    [TestCategory("BVT"), TestCategory("Placement")]
    public class LocalPlacementDirectorTests
    {
        [Fact]
        public async Task PreferLocalPlacementDirector_UsesPlacementHintWhenLocalSiloIsCompatible()
        {
            var localSilo = Silo("127.0.0.1:100@1");
            var hintedSilo = Silo("127.0.0.1:101@1");
            var director = new PreferLocalPlacementDirector();
            var placementContext = CreatePlacementContext(localSilo, SiloStatus.Active, localSilo, hintedSilo);
            var target = CreateTarget(hintedSilo);

            var result = await director.OnAddActivation(strategy: null!, target, placementContext);

            Assert.Equal(hintedSilo, result);
        }

        [Fact]
        public async Task StatelessWorkerDirector_UsesPlacementHintWhenLocalSiloIsCompatible()
        {
            var localSilo = Silo("127.0.0.1:100@1");
            var hintedSilo = Silo("127.0.0.1:101@1");
            var director = new StatelessWorkerDirector();
            var placementContext = CreatePlacementContext(localSilo, SiloStatus.Active, localSilo, hintedSilo);
            var target = CreateTarget(hintedSilo);

            var result = await director.OnAddActivation(strategy: null!, target, placementContext);

            Assert.Equal(hintedSilo, result);
        }

        private static IPlacementContext CreatePlacementContext(SiloAddress localSilo, SiloStatus localSiloStatus, params SiloAddress[] compatibleSilos)
        {
            var placementContext = Substitute.For<IPlacementContext>();
            placementContext.LocalSilo.Returns(localSilo);
            placementContext.LocalSiloStatus.Returns(localSiloStatus);
            placementContext.GetCompatibleSilos(Arg.Any<PlacementTarget>()).Returns(compatibleSilos);
            return placementContext;
        }

        private static PlacementTarget CreateTarget(SiloAddress placementHint) =>
            new(
                GrainId.Create("test", "grain-1"),
                new Dictionary<string, object> { [IPlacementDirector.PlacementHintKey] = placementHint },
                default,
                0);

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);
    }
}
