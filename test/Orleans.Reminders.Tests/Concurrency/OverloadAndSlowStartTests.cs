#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Orleans.Reminders.Concurrency;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Xunit;

namespace UnitTests.Concurrency;

/// <summary>
/// Unit tests for the silo-overload backoff phase of <see cref="LocalReminderDeliveryThrottle"/>.
/// </summary>
public sealed class OverloadBackoffTests
{
    [Fact, TestCategory("BVT")]
    public async Task RespectOverload_SkipImmediately_SkipsWhenOverloaded()
    {
        var detector = new FakeOverloadDetector { IsOverloaded = true };
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(10)
            .RespectOverload(ThrottleBlockMode.SkipImmediately)
            .Build();
        await using var throttle = new TestThrottle(config, overloadDetector: detector);

        var lease = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);

        Assert.Equal(ReminderAdmissionOutcome.Skipped, lease.Outcome);
        Assert.Equal(ReminderSkipReason.SiloOverloaded, lease.SkipReason);
        Assert.Equal(10, throttle.AvailableConcurrencyPermits); // overload skip didn't consume any permit
    }

    [Fact, TestCategory("BVT")]
    public async Task RespectOverload_NotConfigured_IgnoresOverloadDetector()
    {
        var detector = new FakeOverloadDetector { IsOverloaded = true };
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(10)
            .Build();
        await using var throttle = new TestThrottle(config, overloadDetector: detector);

        var lease = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);

        Assert.Equal(ReminderAdmissionOutcome.Admitted, lease.Outcome);
        lease.Dispose();
    }

    [Fact, TestCategory("BVT")]
    public async Task RespectOverload_Wait_AdmitsAfterOverloadClears()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var detector = new FakeOverloadDetector { IsOverloaded = true };
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(10)
            .RespectOverload(ThrottleBlockMode.Wait, pollInterval: TimeSpan.FromMilliseconds(100))
            .Build();
        await using var throttle = new TestThrottle(config, clock, overloadDetector: detector);

        var acquireTask = throttle.AcquireAsync(TestContext.Default(), CancellationToken.None).AsTask();
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(acquireTask.IsCompleted);

        detector.IsOverloaded = false;
        clock.Advance(TimeSpan.FromMilliseconds(200));

        var lease = await acquireTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReminderAdmissionOutcome.Admitted, lease.Outcome);
        lease.Dispose();
    }

    [Fact, TestCategory("BVT")]
    public async Task RespectOverload_WaitUpTo_SkipsAfterTimeout()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var detector = new FakeOverloadDetector { IsOverloaded = true };
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(10)
            .RespectOverload(ThrottleBlockMode.WaitUpTo(TimeSpan.FromMilliseconds(500)), pollInterval: TimeSpan.FromMilliseconds(100))
            .Build();
        await using var throttle = new TestThrottle(config, clock, overloadDetector: detector);

        var acquireTask = throttle.AcquireAsync(TestContext.Default(), CancellationToken.None).AsTask();

        for (var i = 0; i < 10 && !acquireTask.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(10);
        }

        var lease = await acquireTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReminderAdmissionOutcome.Skipped, lease.Outcome);
        Assert.Equal(ReminderSkipReason.SiloOverloaded, lease.SkipReason);
        Assert.Equal(10, throttle.AvailableConcurrencyPermits);
    }

    [Fact, TestCategory("BVT")]
    public void RespectOverload_RequiresOverloadDetector_OrThrowsAtConstruction()
    {
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(10)
            .RespectOverload(ThrottleBlockMode.Wait)
            .Build();

        var ex = Assert.Throws<ArgumentException>(() =>
            new LocalReminderDeliveryThrottle(config, TimeProvider.System, tierName: "test", overloadDetector: null));
        Assert.Contains("IOverloadDetector", ex.Message);
    }

    [Fact, TestCategory("BVT")]
    public async Task RespectOverload_AsSoleTier_IsValid()
    {
        var detector = new FakeOverloadDetector { IsOverloaded = false };
        var config = new ReminderThrottleConfigBuilder()
            .RespectOverload(ThrottleBlockMode.SkipImmediately)
            .Build();
        await using var throttle = new TestThrottle(config, overloadDetector: detector);

        var lease = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);
        Assert.Equal(ReminderAdmissionOutcome.Admitted, lease.Outcome);
    }
}

/// <summary>
/// Unit tests for the slow-start ramp-up phase of <see cref="LocalReminderDeliveryThrottle"/>.
/// </summary>
public sealed class SlowStartTests
{
    [Fact, TestCategory("BVT")]
    public async Task SlowStart_StartsAtInitialCapacity_AndBlocksBeyond()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(10)
            .SlowStart(initialCapacity: 2, interval: TimeSpan.FromSeconds(10), onCapacityExceeded: ThrottleBlockMode.SkipImmediately)
            .Build();
        await using var throttle = new TestThrottle(config, clock);

        Assert.Equal(2, throttle.SlowStartCurrentCapacity);

        var l1 = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);
        var l2 = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);
        var l3 = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);

        Assert.Equal(ReminderAdmissionOutcome.Admitted, l1.Outcome);
        Assert.Equal(ReminderAdmissionOutcome.Admitted, l2.Outcome);
        Assert.Equal(ReminderAdmissionOutcome.Skipped, l3.Outcome);
        Assert.Equal(ReminderSkipReason.SlowStartLimited, l3.SkipReason);

        l1.Dispose();
        l2.Dispose();
    }

    [Fact, TestCategory("BVT")]
    public async Task SlowStart_RampsUpOverTime()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(16)
            .SlowStart(initialCapacity: 2, interval: TimeSpan.FromSeconds(1), onCapacityExceeded: ThrottleBlockMode.SkipImmediately)
            .Build();
        await using var throttle = new TestThrottle(config, clock);

        Assert.Equal(2, throttle.SlowStartCurrentCapacity);

        // 2 -> 4 after one interval.
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForCapacityAsync(throttle, expected: 4);

        // 4 -> 8.
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForCapacityAsync(throttle, expected: 8);

        // 8 -> 16 (clamped at MaxConcurrent).
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForCapacityAsync(throttle, expected: 16);

        // Should remain at 16, no further growth.
        clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Equal(16, throttle.SlowStartCurrentCapacity);
    }

    [Fact, TestCategory("BVT")]
    public async Task SlowStart_WaitMode_AdmitsAfterRampOpensCapacity()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(10)
            .SlowStart(initialCapacity: 1, interval: TimeSpan.FromSeconds(1), onCapacityExceeded: ThrottleBlockMode.Wait)
            .Build();
        await using var throttle = new TestThrottle(config, clock);

        // Consume the only initial permit; do NOT dispose so the slow-start semaphore stays exhausted.
        var l1 = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);
        Assert.Equal(ReminderAdmissionOutcome.Admitted, l1.Outcome);

        var nextTask = throttle.AcquireAsync(TestContext.Default(), CancellationToken.None).AsTask();
        Assert.False(nextTask.IsCompleted);

        // Advance to trigger the first doubling (1 -> 2). The waiter should now get a permit.
        clock.Advance(TimeSpan.FromSeconds(1));

        var lease = await nextTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReminderAdmissionOutcome.Admitted, lease.Outcome);

        l1.Dispose();
        lease.Dispose();
    }

    [Fact, TestCategory("BVT")]
    public async Task SlowStart_WaitUpTo_SkipsIfRampDoesNotOpenInTime()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(10)
            .SlowStart(
                initialCapacity: 1,
                interval: TimeSpan.FromSeconds(10),
                onCapacityExceeded: ThrottleBlockMode.WaitUpTo(TimeSpan.FromMilliseconds(500)))
            .Build();
        await using var throttle = new TestThrottle(config, clock);

        var l1 = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);

        var nextTask = throttle.AcquireAsync(TestContext.Default(), CancellationToken.None).AsTask();
        clock.Advance(TimeSpan.FromMilliseconds(600));
        var lease = await nextTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ReminderAdmissionOutcome.Skipped, lease.Outcome);
        Assert.Equal(ReminderSkipReason.SlowStartLimited, lease.SkipReason);

        l1.Dispose();
    }

    [Fact, TestCategory("BVT")]
    public void SlowStart_RejectedWithoutMaxConcurrent()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ReminderThrottleConfigBuilder()
            .PermitsPerSecond(10)
            .SlowStart(1, TimeSpan.FromSeconds(1), ThrottleBlockMode.Wait)
            .Build());
        Assert.Contains("SlowStart requires MaxConcurrent", ex.Message);
    }

    [Fact, TestCategory("BVT")]
    public void SlowStart_RejectedWhenInitialExceedsMaxConcurrent()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ReminderThrottleConfigBuilder()
            .MaxConcurrent(5)
            .SlowStart(initialCapacity: 10, interval: TimeSpan.FromSeconds(1), onCapacityExceeded: ThrottleBlockMode.Wait)
            .Build());
        Assert.Contains("InitialCapacity", ex.Message);
    }

    [Fact, TestCategory("BVT")]
    public void SlowStart_RejectsZeroInitialCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReminderThrottleConfigBuilder()
            .MaxConcurrent(10)
            .SlowStart(initialCapacity: 0, interval: TimeSpan.FromSeconds(1), onCapacityExceeded: ThrottleBlockMode.Wait)
            .Build());
    }

    [Fact, TestCategory("BVT")]
    public void SlowStart_RejectsZeroInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReminderThrottleConfigBuilder()
            .MaxConcurrent(10)
            .SlowStart(initialCapacity: 1, interval: TimeSpan.Zero, onCapacityExceeded: ThrottleBlockMode.Wait)
            .Build());
    }

    private static async Task WaitForCapacityAsync(TestThrottle throttle, int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (throttle.SlowStartCurrentCapacity != expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(expected, throttle.SlowStartCurrentCapacity);
    }
}

internal sealed class FakeOverloadDetector : IOverloadDetector
{
    public bool IsOverloaded { get; set; }
}
