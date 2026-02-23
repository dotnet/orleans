using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Xunit;

namespace UnitTests.General;

/// <summary>
/// Tests for resource optimized placement configuration options validation.
/// </summary>
public sealed class ResourceOptimizedPlacementOptionsTests
{
    [Fact, TestCategory("PlacementOptions"), TestCategory("BVT")]
    public void ConstantsShouldNotChange()
    {
        Assert.Equal(40, ResourceOptimizedPlacementOptions.DEFAULT_CPU_USAGE_WEIGHT);
        Assert.Equal(20, ResourceOptimizedPlacementOptions.DEFAULT_MEMORY_USAGE_WEIGHT);
        Assert.Equal(20, ResourceOptimizedPlacementOptions.DEFAULT_AVAILABLE_MEMORY_WEIGHT);
        Assert.Equal(5, ResourceOptimizedPlacementOptions.DEFAULT_MAX_AVAILABLE_MEMORY_WEIGHT);
        Assert.Equal(15, ResourceOptimizedPlacementOptions.DEFAULT_ACTIVATION_COUNT_WEIGHT);
    }

    [Fact, TestCategory("PlacementOptions"), TestCategory("BVT")]
    public void DefaultShouldEqualConstants()
    {
        var options = new ResourceOptimizedPlacementOptions();
        Assert.Equal(ResourceOptimizedPlacementOptions.DEFAULT_CPU_USAGE_WEIGHT, options.CpuUsageWeight);
        Assert.Equal(ResourceOptimizedPlacementOptions.DEFAULT_MEMORY_USAGE_WEIGHT, options.MemoryUsageWeight);
        Assert.Equal(ResourceOptimizedPlacementOptions.DEFAULT_AVAILABLE_MEMORY_WEIGHT, options.AvailableMemoryWeight);
        Assert.Equal(ResourceOptimizedPlacementOptions.DEFAULT_MAX_AVAILABLE_MEMORY_WEIGHT, options.MaxAvailableMemoryWeight);
        Assert.Equal(ResourceOptimizedPlacementOptions.DEFAULT_ACTIVATION_COUNT_WEIGHT, options.ActivationCountWeight);
    }

    [Theory, TestCategory("PlacementOptions"), TestCategory("BVT")]
    [InlineData(-10, 0, 0, 0, 0, 0)]
    [InlineData(101, 0, 0, 0, 0, 0)]
    [InlineData(0, -10, 0, 0, 0, 0)]
    [InlineData(0, 101, 0, 0, 0, 0)]
    [InlineData(0, 0, -10, 0, 0, 0)]
    [InlineData(0, 0, 101, 0, 0, 0)]
    [InlineData(0, 0, 0, -10, 0, 0)]
    [InlineData(0, 0, 0, 101, 0, 0)]
    [InlineData(0, 0, 0, 0, -10, 0)]
    [InlineData(0, 0, 0, 0, 101, 0)]
    [InlineData(0, 0, 0, 0, 0, -10)]
    [InlineData(0, 0, 0, 0, 0, 101)]
    public void InvalidWeightsShouldThrow(int cpuUsage, int memUsage, int memAvailable, int maxMemAvailable, int activationCount, int prefMargin)
    {
        var options = Options.Create(new ResourceOptimizedPlacementOptions
        {
            CpuUsageWeight = cpuUsage,
            MemoryUsageWeight = memUsage,
            AvailableMemoryWeight = memAvailable,
            MaxAvailableMemoryWeight = maxMemAvailable,
            LocalSiloPreferenceMargin = prefMargin,
            ActivationCountWeight = activationCount
        });

        var validator = new ResourceOptimizedPlacementOptionsValidator(options);
        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }

    [Theory, TestCategory("PlacementOptions"), TestCategory("BVT")]
    [InlineData(10, 0, 0, 0, 0, 10)]
    [InlineData(100, 0, 0, 0, 0, 10)]
    [InlineData(10, 10, 0, 0, 0, 0)]
    [InlineData(10, 100, 0, 0, 0, 0)]
    [InlineData(10, 0, 10, 0, 0, 0)]
    [InlineData(10, 0, 100, 0, 0, 0)]
    [InlineData(10, 0, 0, 10, 0, 0)]
    [InlineData(10, 0, 0, 100, 0, 0)]
    [InlineData(10, 0, 0, 0, 10, 0)]
    [InlineData(10, 0, 0, 0, 100, 0)]
    [InlineData(10, 0, 0, 0, 0, 100)]
    public void ValidWeightsShouldNotThrow(int cpuUsage, int memUsage, int memAvailable, int maxMemAvailable, int activationCount, int prefMargin)
    {
        var options = Options.Create(new ResourceOptimizedPlacementOptions
        {
            CpuUsageWeight = cpuUsage,
            MemoryUsageWeight = memUsage,
            AvailableMemoryWeight = memAvailable,
            MaxAvailableMemoryWeight = maxMemAvailable,
            LocalSiloPreferenceMargin = prefMargin,
            ActivationCountWeight = activationCount
        });

        var validator = new ResourceOptimizedPlacementOptionsValidator(options);
        validator.ValidateConfiguration();
    }
}
