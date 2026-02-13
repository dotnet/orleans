using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Xunit;

namespace UnitTests.Runtime
{
    [TestCategory("BVT"), TestCategory("Placement")]
    public class ActivationCountPlacementDirectorTests
    {
        [Fact]
        public async Task OnAddActivation_WhenCacheIsEmptyAndLocalSiloIsIncompatible_PlacesOnCompatibleSilo()
        {
            var localSilo = Silo("127.0.0.1:100@1");
            var compatibleSilo = Silo("127.0.0.1:101@1");
            var director = CreateDirector(localSilo);
            var placementContext = Substitute.For<IPlacementContext>();
            placementContext.GetCompatibleSilos(Arg.Any<PlacementTarget>()).Returns([compatibleSilo]);

            var result = await director.OnAddActivation(strategy: null!, target: default, placementContext);

            Assert.Equal(compatibleSilo, result);
        }

        [Fact]
        public async Task OnAddActivation_WhenCacheIsEmptyAndLocalSiloIsCompatible_PlacesLocally()
        {
            var localSilo = Silo("127.0.0.1:100@1");
            var compatibleSilo = Silo("127.0.0.1:101@1");
            var director = CreateDirector(localSilo);
            var placementContext = Substitute.For<IPlacementContext>();
            placementContext.GetCompatibleSilos(Arg.Any<PlacementTarget>()).Returns([compatibleSilo, localSilo]);

            var result = await director.OnAddActivation(strategy: null!, target: default, placementContext);

            Assert.Equal(localSilo, result);
        }

        private static ActivationCountPlacementDirector CreateDirector(SiloAddress localSilo)
        {
            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            localSiloDetails.SiloAddress.Returns(localSilo);

            return new ActivationCountPlacementDirector(
                localSiloDetails,
                deploymentLoadPublisher: null!,
                Options.Create(new ActivationCountBasedPlacementOptions()));
        }

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);
    }
}
