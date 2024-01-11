using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Runtime.Configuration.Options;
using TestExtensions;
using Xunit;

namespace UnitTests.General
{
    public class PlacementOptionsTest : OrleansTestingBase, IClassFixture<LoadSheddingTest.Fixture>
    {
        [Fact, TestCategory("Placement"), TestCategory("Functional")]
        public void ConstantsShouldNotChange()
        {
            Assert.Equal(0.4f, ResourceOptimizedPlacementOptions.DEFAULT_CPU_USAGE_WEIGHT);
            Assert.Equal(0.3f, ResourceOptimizedPlacementOptions.DEFAULT_MEMORY_USAGE_WEIGHT);
            Assert.Equal(0.2f, ResourceOptimizedPlacementOptions.DEFAULT_AVAILABLE_MEMORY_WEIGHT);
            Assert.Equal(0.1f, ResourceOptimizedPlacementOptions.DEFAULT_PHYSICAL_MEMORY_WEIGHT);
        }

        [Theory, TestCategory("Placement"), TestCategory("Functional")]
        [InlineData(-0.1f, 0.4f, 0.2f, 0.1f, 0.05f)]
        [InlineData(0.3f, 1.1f, 0.2f, 0.1f, 0.05f)]
        [InlineData(0.3f, 0.4f, -0.1f, 0.1f, 0.05f)]
        [InlineData(0.3f, 0.4f, 0.2f, 1.1f, 0.05f)]
        [InlineData(0.3f, 0.4f, 0.2f, 0.1f, -0.05f)]
        public void InvalidWeightsShouldThrow(float cpuUsage, float memUsage, float memAvailable, float memPhysical, float prefMargin)
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

        [Fact, TestCategory("Placement"), TestCategory("Functional")]
        public void SumGreaterThanOneShouldThrow()
        {
            var options = Options.Create(new ResourceOptimizedPlacementOptions
            {
                CpuUsageWeight = 0.3f,
                MemoryUsageWeight = 0.4f,
                AvailableMemoryWeight = 0.2f,
                PhysicalMemoryWeight = 0.21f // sum > 1
            });

            var validator = new ResourceOptimizedPlacementOptionsValidator(options);
            Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
        }

        [Fact, TestCategory("Placement"), TestCategory("Functional")]
        public void SumLessThanOneShouldThrow()
        {
            var options = Options.Create(new ResourceOptimizedPlacementOptions
            {
                CpuUsageWeight = 0.3f,
                MemoryUsageWeight = 0.4f,
                AvailableMemoryWeight = 0.2f,
                PhysicalMemoryWeight = 0.19f // sum < 1
            });

            var validator = new ResourceOptimizedPlacementOptionsValidator(options);
            Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
        }
    }
}
