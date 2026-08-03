#nullable enable
using System;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime;
using Orleans.Runtime.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Tests;

[TestCategory("BVT")]
public class DurableTaskGrainRuntimeSharedTests
{
    [Fact]
    public void CleanupPolicy_CleanupAge_GetSetRoundTrips()
    {
        var policy = new CleanupPolicy();

        // Default value for an unset TimeSpan property is TimeSpan.Zero.
        Assert.Equal(TimeSpan.Zero, policy.CleanupAge);

        policy.CleanupAge = TimeSpan.FromHours(6);

        Assert.Equal(TimeSpan.FromHours(6), policy.CleanupAge);
    }

    [Fact]
    public void Constructor_WiresGrainContextAccessorTimeProviderAndLoggerFromArguments()
    {
        var grainContext = new TestGrainContext(GrainId.Create("grain-type", "grain-key"));
        var accessor = new TestGrainContextAccessor(grainContext);
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var logger = NullLogger<DurableTaskGrainRuntime>.Instance;

        var shared = new DurableTaskGrainRuntimeShared(accessor, timeProvider, logger);

        Assert.Same(accessor, shared.GrainContextAccessor);
        Assert.Same(timeProvider, shared.TimeProvider);
        Assert.Same(logger, shared.Logger);
        Assert.Same(grainContext, shared.GrainContextAccessor.GrainContext);
    }

    [Fact]
    public void Constructor_DefaultCleanupPolicy_IsOneDay()
    {
        var accessor = new TestGrainContextAccessor(new TestGrainContext(GrainId.Create("grain-type", "grain-key")));
        var timeProvider = new FakeTimeProvider();
        var logger = NullLogger<DurableTaskGrainRuntime>.Instance;

        var shared = new DurableTaskGrainRuntimeShared(accessor, timeProvider, logger);

        Assert.Equal(TimeSpan.FromDays(1), shared.DefaultCleanupPolicy.CleanupAge);
    }

    [Fact]
    public void DefaultCleanupPolicy_IsANewMutableInstancePerConstructorCall()
    {
        var accessor = new TestGrainContextAccessor(new TestGrainContext(GrainId.Create("grain-type", "grain-key")));
        var timeProvider = new FakeTimeProvider();
        var logger = NullLogger<DurableTaskGrainRuntime>.Instance;

        var sharedA = new DurableTaskGrainRuntimeShared(accessor, timeProvider, logger);
        var sharedB = new DurableTaskGrainRuntimeShared(accessor, timeProvider, logger);

        sharedA.DefaultCleanupPolicy.CleanupAge = TimeSpan.FromMinutes(1);

        Assert.NotSame(sharedA.DefaultCleanupPolicy, sharedB.DefaultCleanupPolicy);
        Assert.Equal(TimeSpan.FromMinutes(1), sharedA.DefaultCleanupPolicy.CleanupAge);
        Assert.Equal(TimeSpan.FromDays(1), sharedB.DefaultCleanupPolicy.CleanupAge);
    }
}
