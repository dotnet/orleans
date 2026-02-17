using System;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.Statistics;
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

        [Fact]
        public async Task OnAddActivation_WhenSomeCompatibleSilosHaveNoStats_PrefersSilosWithStats()
        {
            var localSilo = Silo("127.0.0.1:100@1");
            var siloWithStats = Silo("127.0.0.1:101@1");
            var siloWithoutStats = Silo("127.0.0.1:102@1");
            var director = CreateDirector(localSilo);
            var placementContext = CreatePlacementContext(siloWithStats, siloWithoutStats);
            director.SiloStatisticsChangeNotification(siloWithStats, CreateSiloRuntimeStatistics(overloaded: false, recentlyUsedActivationCount: 10));

            var result = await director.OnAddActivation(strategy: null!, target: default, placementContext);

            Assert.Equal(siloWithStats, result);
        }

        [Fact]
        public async Task OnAddActivation_WhenAllCompatibleSilosWithStatsAreOverloaded_Throws()
        {
            var localSilo = Silo("127.0.0.1:100@1");
            var overloadedSilo1 = Silo("127.0.0.1:101@1");
            var overloadedSilo2 = Silo("127.0.0.1:102@1");
            var director = CreateDirector(localSilo);
            var placementContext = CreatePlacementContext(overloadedSilo1, overloadedSilo2);
            director.SiloStatisticsChangeNotification(overloadedSilo1, CreateSiloRuntimeStatistics(overloaded: true));
            director.SiloStatisticsChangeNotification(overloadedSilo2, CreateSiloRuntimeStatistics(overloaded: true));

            await Assert.ThrowsAsync<SiloUnavailableException>(() => director.OnAddActivation(strategy: null!, target: default, placementContext));
        }

        [Fact]
        public async Task OnAddActivation_WhenSilosWithStatsAreOverloadedAndWithoutStatsExist_FallsBackToWithoutStats()
        {
            var localSilo = Silo("127.0.0.1:100@1");
            var overloadedSilo = Silo("127.0.0.1:101@1");
            var siloWithoutStats = Silo("127.0.0.1:102@1");
            var director = CreateDirector(localSilo);
            var placementContext = CreatePlacementContext(overloadedSilo, siloWithoutStats);
            director.SiloStatisticsChangeNotification(overloadedSilo, CreateSiloRuntimeStatistics(overloaded: true));

            var result = await director.OnAddActivation(strategy: null!, target: default, placementContext);

            Assert.Equal(siloWithoutStats, result);
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

        private static IPlacementContext CreatePlacementContext(params SiloAddress[] compatibleSilos)
        {
            var placementContext = Substitute.For<IPlacementContext>();
            placementContext.GetCompatibleSilos(Arg.Any<PlacementTarget>()).Returns(compatibleSilos);
            return placementContext;
        }

        private static SiloRuntimeStatistics CreateSiloRuntimeStatistics(bool overloaded, int recentlyUsedActivationCount = 0)
        {
            var environmentStatisticsProvider = Substitute.For<IEnvironmentStatisticsProvider>();
            var maxMemoryBytes = 1000L;
            var memoryUsageBytes = overloaded ? 950L : 100L;
            var availableMemoryBytes = maxMemoryBytes - memoryUsageBytes;
            var cpuUsagePercentage = overloaded ? 100f : 0f;

            environmentStatisticsProvider.GetEnvironmentStatistics().Returns(
                new EnvironmentStatistics(
                    cpuUsagePercentage: cpuUsagePercentage,
                    rawCpuUsagePercentage: cpuUsagePercentage,
                    memoryUsageBytes: memoryUsageBytes,
                    rawMemoryUsageBytes: memoryUsageBytes,
                    availableMemoryBytes: availableMemoryBytes,
                    rawAvailableMemoryBytes: availableMemoryBytes,
                    maximumAvailableMemoryBytes: maxMemoryBytes));

            return new SiloRuntimeStatistics(
                activationCount: 0,
                recentlyUsedActivationCount: recentlyUsedActivationCount,
                environmentStatisticsProvider,
                Options.Create(new LoadSheddingOptions { LoadSheddingEnabled = overloaded }),
                DateTime.UtcNow);
        }

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);
    }
}
