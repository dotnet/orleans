#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Orleans.Reminders.Concurrency;
using Orleans.Runtime;
using Xunit;

namespace UnitTests.Concurrency;

public sealed class ThrottleBlockModeTests
{
    [Fact, TestCategory("BVT")]
    public void Wait_IsSingleton()
    {
        Assert.Same(ThrottleBlockMode.Wait, ThrottleBlockMode.Wait);
    }

    [Fact, TestCategory("BVT")]
    public void SkipImmediately_IsSingleton()
    {
        Assert.Same(ThrottleBlockMode.SkipImmediately, ThrottleBlockMode.SkipImmediately);
    }

    [Fact, TestCategory("BVT")]
    public void WaitUpTo_RejectsZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ThrottleBlockMode.WaitUpTo(TimeSpan.Zero));
    }

    [Fact, TestCategory("BVT")]
    public void WaitUpTo_RejectsNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ThrottleBlockMode.WaitUpTo(TimeSpan.FromSeconds(-1)));
    }

    [Fact, TestCategory("BVT")]
    public void WaitUpTo_ProducesDistinctValuesForDistinctTimeouts()
    {
        Assert.NotEqual(ThrottleBlockMode.WaitUpTo(TimeSpan.FromSeconds(1)), ThrottleBlockMode.WaitUpTo(TimeSpan.FromSeconds(2)));
    }
}

public sealed class ThrottleConfigTests
{
    [Fact, TestCategory("BVT")]
    public void Builder_RejectsEmptyConfig()
    {
        var b = new ReminderThrottleConfigBuilder();
        Assert.Throws<ArgumentException>(() => b.Build());
    }

    [Fact, TestCategory("BVT")]
    public void Builder_RejectsZeroMaxConcurrent()
    {
        var b = new ReminderThrottleConfigBuilder().MaxConcurrent(0);
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Build());
    }

    [Fact, TestCategory("BVT")]
    public void Builder_RejectsNegativeRate()
    {
        var b = new ReminderThrottleConfigBuilder().PermitsPerSecond(-1.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Build());
    }

    [Fact, TestCategory("BVT")]
    public void Builder_RejectsNonFiniteRate()
    {
        var b = new ReminderThrottleConfigBuilder().PermitsPerSecond(double.PositiveInfinity);
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Build());
    }

    [Fact, TestCategory("BVT")]
    public void Builder_RejectsBurstWithoutRate()
    {
        var b = new ReminderThrottleConfigBuilder().MaxConcurrent(5).BurstSize(10);
        Assert.Throws<ArgumentException>(() => b.Build());
    }

    [Fact, TestCategory("BVT")]
    public void Builder_AutoDerivesBurstSizeFromRate()
    {
        var c = new ReminderThrottleConfigBuilder().PermitsPerSecond(7.5).Build();
        Assert.Equal(8, c.BurstSize);
    }

    [Fact, TestCategory("BVT")]
    public void Builder_HonorsExplicitBurstSize()
    {
        var c = new ReminderThrottleConfigBuilder().PermitsPerSecond(10).BurstSize(100).Build();
        Assert.Equal(100, c.BurstSize);
    }

    [Fact, TestCategory("BVT")]
    public void Builder_DefaultsBlockModeToWait()
    {
        var c = new ReminderThrottleConfigBuilder().MaxConcurrent(1).Build();
        Assert.Same(ThrottleBlockMode.Wait, c.BlockMode);
    }
}

public sealed class NoOpThrottleTests
{
    [Fact, TestCategory("BVT")]
    public async Task AcquireAsync_AlwaysReturnsSharedAdmittedLease()
    {
        var throttle = NoOpReminderDeliveryThrottle.Instance;
        var ctx = TestContext.Default();

        var l1 = await throttle.AcquireAsync(ctx, CancellationToken.None);
        var l2 = await throttle.AcquireAsync(ctx, CancellationToken.None);

        Assert.Equal(ReminderAdmissionOutcome.Admitted, l1.Outcome);
        Assert.Equal(ReminderAdmissionOutcome.Admitted, l2.Outcome);
        Assert.Same(ReminderDeliveryLease.NoOpAdmitted, l1);
        Assert.Same(l1, l2);
        Assert.Equal(TimeSpan.Zero, l1.WaitedFor);
        Assert.Null(l1.SkipReason);
        Assert.Null(l1.TierName);
    }

    [Fact, TestCategory("BVT")]
    public async Task Dispose_OnNoOpLease_IsIdempotentNoOp()
    {
        var lease = await NoOpReminderDeliveryThrottle.Instance.AcquireAsync(TestContext.Default(), CancellationToken.None);
        lease.Dispose();
        lease.Dispose();
        // No exception, no state change to verify other than not throwing.
    }
}

public sealed class LocalThrottleConcurrencyTests
{
    [Fact, TestCategory("BVT")]
    public async Task MaxConcurrent_BoundsInFlight()
    {
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(2)
            .BlockMode(ThrottleBlockMode.Wait)
            .Build();
        await using var throttle = new TestThrottle(config);

        var l1 = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);
        var l2 = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);
        Assert.Equal(0, throttle.AvailableConcurrencyPermits);

        var l3Task = throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);
        Assert.False(l3Task.IsCompleted);

        l1.Dispose();
        var l3 = await l3Task.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ReminderAdmissionOutcome.Admitted, l3.Outcome);

        l2.Dispose();
        l3.Dispose();
        Assert.Equal(2, throttle.AvailableConcurrencyPermits);
    }

    [Fact, TestCategory("BVT")]
    public async Task SkipImmediately_ReturnsSkippedWhenFull()
    {
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(1)
            .BlockMode(ThrottleBlockMode.SkipImmediately)
            .Build();
        await using var throttle = new TestThrottle(config);

        var l1 = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);
        var l2 = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);

        Assert.Equal(ReminderAdmissionOutcome.Admitted, l1.Outcome);
        Assert.Equal(ReminderAdmissionOutcome.Skipped, l2.Outcome);
        Assert.Equal(ReminderSkipReason.LocalLimiterFull, l2.SkipReason);
        Assert.True(l2.WaitedFor < TimeSpan.FromMilliseconds(50));
        Assert.Equal("test", l2.TierName);

        l1.Dispose();
        l2.Dispose(); // No-op for skipped lease.
    }

    [Fact, TestCategory("BVT")]
    public async Task Cancellation_DoesNotConsumePermit()
    {
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(1)
            .BlockMode(ThrottleBlockMode.Wait)
            .Build();
        await using var throttle = new TestThrottle(config);

        var l1 = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var l2Task = throttle.AcquireAsync(TestContext.Default(), cts.Token).AsTask();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => l2Task);

        Assert.Equal(0, throttle.AvailableConcurrencyPermits);
        l1.Dispose();
        Assert.Equal(1, throttle.AvailableConcurrencyPermits);
    }

    [Fact, TestCategory("BVT")]
    public async Task PermitIsSpentOnDisposeRegardlessOfCallerOutcome()
    {
        var config = new ReminderThrottleConfigBuilder().MaxConcurrent(1).Build();
        await using var throttle = new TestThrottle(config);

        var lease = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);
        Assert.Equal(0, throttle.AvailableConcurrencyPermits);
        // Simulate a delivery failure: dispose still returns the permit exactly once.
        lease.Dispose();
        lease.Dispose(); // idempotent
        Assert.Equal(1, throttle.AvailableConcurrencyPermits);
    }

    /// <summary>
    /// Regression for adversarial-review finding: if the rate-acquire phase throws (e.g., the
    /// caller's cancellation token fires while the throttle is waiting on Task.Delay for the
    /// next refill), the concurrency permit acquired in the preceding phase must be released
    /// before the exception propagates. Otherwise the permit is leaked permanently.
    /// </summary>
    [Fact, TestCategory("BVT")]
    public async Task Cancellation_DuringRateWait_ReleasesConcurrencyPermit()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(2)
            .PermitsPerSecond(1)
            .BurstSize(1)
            .BlockMode(ThrottleBlockMode.Wait)
            .Build();
        await using var throttle = new TestThrottle(config, clock);

        // First acquire takes both the rate token and one concurrency permit.
        var l1 = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);
        Assert.Equal(ReminderAdmissionOutcome.Admitted, l1.Outcome);
        Assert.Equal(1, throttle.AvailableConcurrencyPermits);

        // Second acquire reserves a concurrency permit but blocks on the empty rate bucket.
        using var cts = new CancellationTokenSource();
        var l2Task = throttle.AcquireAsync(TestContext.Default(), cts.Token).AsTask();

        // Yield until the throttle has reserved the second concurrency permit and is waiting on
        // the bucket refill. The acquire completes the semaphore wait synchronously and then awaits
        // Task.Delay against the FakeTimeProvider, which never completes until the clock advances.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (throttle.AvailableConcurrencyPermits == 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.Equal(0, throttle.AvailableConcurrencyPermits);
        Assert.False(l2Task.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => l2Task);

        // The cancelled second acquire MUST have released its concurrency permit before propagating.
        Assert.Equal(1, throttle.AvailableConcurrencyPermits);

        l1.Dispose();
        Assert.Equal(2, throttle.AvailableConcurrencyPermits);
    }
}

public sealed class LocalThrottleRateTests
{
    [Fact, TestCategory("BVT")]
    public async Task PermitsPerSecond_AdmitsBurstThenSkips()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ReminderThrottleConfigBuilder()
            .PermitsPerSecond(10)
            .BlockMode(ThrottleBlockMode.SkipImmediately)
            .Build();
        await using var throttle = new TestThrottle(config, clock);

        // Burst of 10 (auto-derived) should be admitted immediately.
        var admitted = 0;
        for (var i = 0; i < 10; i++)
        {
            var l = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);
            if (l.Outcome == ReminderAdmissionOutcome.Admitted)
            {
                admitted++;
            }
            l.Dispose();
        }

        Assert.Equal(10, admitted);

        // 11th immediately is skipped (no tokens left).
        var next = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);
        Assert.Equal(ReminderAdmissionOutcome.Skipped, next.Outcome);
        Assert.Equal(ReminderSkipReason.LocalLimiterFull, next.SkipReason);

        // Advance one second: bucket refills to capacity.
        clock.Advance(TimeSpan.FromSeconds(1));
        var refilled = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);
        Assert.Equal(ReminderAdmissionOutcome.Admitted, refilled.Outcome);
        refilled.Dispose();
    }

    [Fact, TestCategory("BVT")]
    public async Task WaitMode_PacesAcquiresAtConfiguredRate()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ReminderThrottleConfigBuilder()
            .PermitsPerSecond(2)
            .BurstSize(1)
            .BlockMode(ThrottleBlockMode.Wait)
            .Build();
        await using var throttle = new TestThrottle(config, clock);

        var first = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);
        Assert.Equal(ReminderAdmissionOutcome.Admitted, first.Outcome);
        first.Dispose();

        // Next acquire must wait ~0.5s.
        var nextTask = throttle.AcquireAsync(TestContext.Default(), CancellationToken.None).AsTask();
        Assert.False(nextTask.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        var next = await nextTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ReminderAdmissionOutcome.Admitted, next.Outcome);
        next.Dispose();
    }

    /// <summary>
    /// Regression for adversarial-review finding: the rate-acquire deadline math previously
    /// mixed timestamp frequencies across TimeProvider instances. Verify WaitUpTo terminates
    /// in a skip after the configured timeout when driven by FakeTimeProvider (whose
    /// TimestampFrequency may differ from Stopwatch.Frequency).
    /// </summary>
    [Fact, TestCategory("BVT")]
    public async Task WaitUpTo_OnRateLimitedPath_SkipsWhenWaitExceedsTimeout()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ReminderThrottleConfigBuilder()
            .PermitsPerSecond(1)
            .BurstSize(1)
            .BlockMode(ThrottleBlockMode.WaitUpTo(TimeSpan.FromMilliseconds(500)))
            .Build();
        await using var throttle = new TestThrottle(config, clock);

        var l1 = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);
        Assert.Equal(ReminderAdmissionOutcome.Admitted, l1.Outcome);
        l1.Dispose();

        // Bucket is now empty; next refill is ~1 second out. Timeout is 500ms so the throttle
        // must short-circuit to a skip BEFORE waiting, since the computed wait (1s) > timeout (500ms).
        var l2 = await throttle.AcquireAsync(TestContext.Default(), CancellationToken.None);
        Assert.Equal(ReminderAdmissionOutcome.Skipped, l2.Outcome);
        Assert.Equal(ReminderSkipReason.AcquireTimeout, l2.SkipReason);
    }
}

internal sealed class TestThrottle : IAsyncDisposable
{
    private readonly LocalReminderDeliveryThrottle _inner;

    public TestThrottle(ThrottleConfig config, TimeProvider? timeProvider = null)
    {
        _inner = new LocalReminderDeliveryThrottle(config, timeProvider ?? TimeProvider.System, tierName: "test");
    }

    public int AvailableConcurrencyPermits => _inner.AvailableConcurrencyPermits;

    public ValueTask<ReminderDeliveryLease> AcquireAsync(ReminderDeliveryContext ctx, CancellationToken ct)
        => _inner.AcquireAsync(ctx, ct);

    public ValueTask DisposeAsync()
    {
        _inner.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static class TestContext
{
    public static ReminderDeliveryContext Default()
    {
        var grainId = GrainId.Create("test", "grain");
        var now = DateTime.UtcNow;
        var status = new TickStatus(now, TimeSpan.FromMinutes(1), now);
        return new ReminderDeliveryContext(grainId, "test-reminder", status);
    }
}
