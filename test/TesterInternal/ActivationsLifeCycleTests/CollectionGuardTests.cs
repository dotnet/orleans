using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.CollectionGuards;
using Orleans.Statistics;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

public class CollectionGuardTests
{
    private class UnknownGCPressureEnvironmentStatistics : IAppEnvironmentStatistics
    {
        public long? MemoryUsage { get; } = null;
    }

    private class GigabyteAppEnvironmentStatistics : IAppEnvironmentStatistics
    {
        public long? MemoryUsage { get; } = 1_000_000_000;
    }

    private class UnknownHostEnvironmentStatistics : IHostEnvironmentStatistics
    {
        public long? TotalPhysicalMemory { get; } = null;
        public float? CpuUsage { get; } = null;
        public long? AvailableMemory { get; } = null;
    }

    private class GigabyteHostEnvironmentStatistics : IHostEnvironmentStatistics
    {
        public long? TotalPhysicalMemory { get; } = 1_000_000_000;
        public float? CpuUsage { get; } = null;
        public long? AvailableMemory { get; } = 500_000_000;
    }

    [Fact, TestCategory("ActivationCollector"), TestCategory("Functional")]
    public void CollectionGCGuardTest()
    {
        var guard1 = new ProcessMemoryCollectionGuard(new GigabyteAppEnvironmentStatistics(),
            Options.Create(new GrainCollectionOptions
            {
                CollectionGCMemoryThreshold = 1_000_000
            }));
        var guard2 = new ProcessMemoryCollectionGuard(new GigabyteAppEnvironmentStatistics(),
            Options.Create(new GrainCollectionOptions
            {
                CollectionGCMemoryThreshold = 2_000_000_000
            }));
        var guard3 = new ProcessMemoryCollectionGuard(new GigabyteAppEnvironmentStatistics(),
            Options.Create(new GrainCollectionOptions
            {
                CollectionGCMemoryThreshold = null
            }));

        var result1 = guard1.ShouldCollect();
        var result2 = guard2.ShouldCollect();
        var result3 = guard3.ShouldCollect();

        Assert.True(result1);
        Assert.False(result2);
        Assert.True(result3);
    }

    /// <summary>
    /// If the environment statistics are not available, the guard should return true
    /// to err on the side of caution.
    /// </summary>
    [Fact, TestCategory("ActivationCollector"), TestCategory("Functional")]
    public void CollectionGCMissingConfigurationGuardTest()
    {
        var guard1 = new ProcessMemoryCollectionGuard(new UnknownGCPressureEnvironmentStatistics(),
            Options.Create(new GrainCollectionOptions
            {
                CollectionGCMemoryThreshold = 1_000_000
            }));
        var result1 = guard1.ShouldCollect();

        Assert.True(result1);
    }

    [Fact, TestCategory("ActivationCollector"), TestCategory("Functional")]
    public void CollectionSystemMemoryGuardTest()
    {
        var guard1 = new SystemMemoryCollectionGuard(new GigabyteHostEnvironmentStatistics(),
            Options.Create(new GrainCollectionOptions
            {
                CollectionGCMemoryThreshold = 1_000_000
            }));
        var guard2 = new SystemMemoryCollectionGuard(new GigabyteHostEnvironmentStatistics(),
            Options.Create(new GrainCollectionOptions
            {
                CollectionGCMemoryThreshold = 2_000_000_000
            }));
        var guard3 = new SystemMemoryCollectionGuard(new GigabyteHostEnvironmentStatistics(),
            Options.Create(new GrainCollectionOptions
            {
                CollectionGCMemoryThreshold = null
            }));

        var result1 = guard1.ShouldCollect();
        var result2 = guard2.ShouldCollect();
        var result3 = guard3.ShouldCollect();

        Assert.True(result1);
        Assert.False(result2);
        Assert.True(result3);
    }

    [Fact, TestCategory("ActivationCollector"), TestCategory("Functional")]
    public void CollectionSystemMemoryPercentGuardTest()
    {
        var guard1 = new SystemMemoryCollectionGuard(new GigabyteHostEnvironmentStatistics(),
            Options.Create(new GrainCollectionOptions
            {
                CollectionSystemMemoryFreePercentThreshold = 60
            }));
        var guard2 = new SystemMemoryCollectionGuard(new GigabyteHostEnvironmentStatistics(),
            Options.Create(new GrainCollectionOptions
            {
                CollectionSystemMemoryFreePercentThreshold = 50
            }));
        var guard3 = new SystemMemoryCollectionGuard(new GigabyteHostEnvironmentStatistics(),
            Options.Create(new GrainCollectionOptions
            {
                CollectionSystemMemoryFreePercentThreshold = 20
            }));
        var guard4 = new SystemMemoryCollectionGuard(new GigabyteHostEnvironmentStatistics(),
            Options.Create(new GrainCollectionOptions
            {
                CollectionSystemMemoryFreePercentThreshold = null
            }));

        var result1 = guard1.ShouldCollect();
        var result2 = guard2.ShouldCollect();
        var result3 = guard3.ShouldCollect();
        var result4 = guard4.ShouldCollect();

        Assert.True(result1);
        Assert.False(result2);
        Assert.False(result3);
        Assert.True(result4);
    }

    /// <summary>
    /// If the system statistics are not available, the guard should return true
    /// to err on the side of caution.
    /// </summary>
    [Fact, TestCategory("ActivationCollector"), TestCategory("Functional")]
    public void CollectionSystemMissingConfigurationGuardTest()
    {
        var guard1 = new SystemMemoryCollectionGuard(new UnknownHostEnvironmentStatistics(),
            Options.Create(new GrainCollectionOptions
            {
                CollectionSystemMemoryFreeThreshold = 1_000_000
            }));
        var guard2 = new SystemMemoryCollectionGuard(new UnknownHostEnvironmentStatistics(),
            Options.Create(new GrainCollectionOptions
            {
                CollectionSystemMemoryFreeThreshold = null
            }));
        var result1 = guard1.ShouldCollect();
        var result2 = guard2.ShouldCollect();

        Assert.True(result1);
        Assert.True(result2);
    }
}