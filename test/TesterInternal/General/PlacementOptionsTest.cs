using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Runtime.Configuration.Options;
using TestExtensions;
using Xunit;

namespace UnitTests.General
{
    public class PlacementOptionsTest : OrleansTestingBase, IClassFixture<LoadSheddingTest.Fixture>
    {
        [Fact, TestCategory("PlacementOptions"), TestCategory("Functional")]
        public void ConstantsShouldNotChange()
        {
            Assert.Equal(40, ResourceOptimizedPlacementOptions.DEFAULT_CPU_USAGE_WEIGHT);
            Assert.Equal(30, ResourceOptimizedPlacementOptions.DEFAULT_MEMORY_USAGE_WEIGHT);
            Assert.Equal(20, ResourceOptimizedPlacementOptions.DEFAULT_AVAILABLE_MEMORY_WEIGHT);
            Assert.Equal(10, ResourceOptimizedPlacementOptions.DEFAULT_PHYSICAL_MEMORY_WEIGHT);
        }

        [Theory, TestCategory("PlacementOptions"), TestCategory("Functional")]
        [InlineData(-10, 40, 0.2, 0.1, 5)]
        [InlineData(30, -11, 20, 10, 5)]
        [InlineData(30, 40, -10, 10, 5)]
        [InlineData(30, 40, 20, 10, -5)]
        [InlineData(30, 40, 20, 10, 101)]
        public void InvalidWeightsShouldThrow(int cpuUsage, int memUsage, int memAvailable, int memPhysical, int prefMargin)
        {
            var options = Options.Create(new ResourceOptimizedPlacementOptions
            {
                CpuUsageWeight = cpuUsage,
                MemoryUsageWeight = memUsage,
                AvailableMemoryWeight = memAvailable,
                PhysicalMemoryWeight = memPhysical,
                LocalSiloPreferenceMargin = prefMargin
            });

            var validator = new ResourceOptimizedPlacementOptionsValidator(options);
            Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
        }
    }
}
