#nullable enable

using System;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Reminders.Concurrency;
using Orleans.Runtime;
using Xunit;

namespace UnitTests.Concurrency;

public sealed class ThrottleBlockModeTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public void Wait_IsSingleton()
    {
        Assert.Same(ThrottleBlockMode.Wait, ThrottleBlockMode.Wait);
        Assert.Equal("Wait", ThrottleBlockMode.Wait.ToString());
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public void SkipImmediately_IsSingleton()
    {
        Assert.Same(ThrottleBlockMode.SkipImmediately, ThrottleBlockMode.SkipImmediately);
        Assert.Equal("SkipImmediately", ThrottleBlockMode.SkipImmediately.ToString());
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public void WaitUpTo_RejectsZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ThrottleBlockMode.WaitUpTo(TimeSpan.Zero));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public void WaitUpTo_RejectsNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ThrottleBlockMode.WaitUpTo(TimeSpan.FromSeconds(-1)));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public void WaitUpTo_RejectsTimeoutBeyondRuntimeTimerLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ThrottleBlockMode.WaitUpTo(TimeSpan.MaxValue));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public void WaitUpTo_ProducesDistinctValuesForDistinctTimeouts()
    {
        Assert.NotEqual(ThrottleBlockMode.WaitUpTo(TimeSpan.FromSeconds(1)), ThrottleBlockMode.WaitUpTo(TimeSpan.FromSeconds(2)));
        Assert.Equal("WaitUpTo(00:00:01)", ThrottleBlockMode.WaitUpTo(TimeSpan.FromSeconds(1)).ToString());
    }
}

public sealed class ReminderThrottleInstrumentsTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public void ActiveLeases_ReportsCurrentValue_WhenListenerStartsAfterAcquire()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var orleansInstruments = new OrleansInstruments(serviceProvider.GetRequiredService<IMeterFactory>());
        var instruments = new ReminderThrottleInstruments(orleansInstruments);
        instruments.OnLeaseAcquired("test");

        int? activeLeases = null;
        string? tier = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, orleansInstruments.Meter)
                && instrument.Name == "orleans-reminders-throttle-active-leases")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, measurement, tags, _) =>
        {
            activeLeases = measurement;
            foreach (var tag in tags)
            {
                if (tag.Key == ReminderActivityAttributes.ThrottleTier)
                {
                    tier = tag.Value?.ToString();
                }
            }
        });

        listener.Start();
        listener.RecordObservableInstruments();

        Assert.Equal(1, activeLeases);
        Assert.Equal("test", tier);

        instruments.OnLeaseReleased("test");
        listener.RecordObservableInstruments();

        Assert.Equal(0, activeLeases);
    }
}

public sealed class AdmissionTransactionTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task CancellationAfterRateReservation_RestoresToken()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ReminderThrottleConfigBuilder()
            .PermitsPerSecond(1, 1, ThrottleBlockMode.Wait)
            .Build();
        using var gate = new LocalRateReminderAdmissionGate(config.Rate!, clock);
        using var cancellation = new CancellationTokenSource();
        using var transaction = new ReminderAdmissionTransaction(cancellation.Token);
        var budget = new ReminderAcquireBudget(clock, timeout: null);

        var result = await gate.AcquireAsync(ReminderDeliveryTestContext.Default(), budget, cancellation.Token);
        Assert.True(result.AdmittedLease);
        Assert.True(transaction.TryAdd(result.Reservation));
        Assert.Equal(0, gate.AvailableTokens);

        cancellation.Cancel();

        Assert.Equal(1, gate.AvailableTokens);
        Assert.Equal(
            ReminderAdmissionCommitOutcome.Cancelled,
            transaction.TryCommit(budget, cancellation.Token, out _));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task MixedReservations_RollBackSemaphoreAndRateCapacity()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(1, ThrottleBlockMode.Wait)
            .PermitsPerSecond(1, 1, ThrottleBlockMode.Wait)
            .Build();
        using var concurrencyGate = new LocalConcurrencyReminderAdmissionGate(config.Concurrency!, clock);
        using var rateGate = new LocalRateReminderAdmissionGate(config.Rate!, clock);
        using var transaction = new ReminderAdmissionTransaction(CancellationToken.None);
        var budget = new ReminderAcquireBudget(clock, timeout: null);

        var concurrency = await concurrencyGate.AcquireAsync(ReminderDeliveryTestContext.Default(), budget, CancellationToken.None);
        var rate = await rateGate.AcquireAsync(ReminderDeliveryTestContext.Default(), budget, CancellationToken.None);
        Assert.True(transaction.TryAdd(concurrency.Reservation));
        Assert.True(transaction.TryAdd(rate.Reservation));
        Assert.Equal(0, concurrencyGate.AvailablePermits);
        Assert.Equal(0, rateGate.AvailableTokens);

        transaction.Rollback();

        Assert.Equal(1, concurrencyGate.AvailablePermits);
        Assert.Equal(1, rateGate.AvailableTokens);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task FinalDeadlineCheck_RollsBackFastFinalGate()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(1, ThrottleBlockMode.Wait)
            .PermitsPerSecond(1, 1, ThrottleBlockMode.WaitUpTo(TimeSpan.FromMilliseconds(500)))
            .Build();
        using var concurrencyGate = new LocalConcurrencyReminderAdmissionGate(config.Concurrency!, clock);
        using var rateGate = new LocalRateReminderAdmissionGate(config.Rate!, clock);
        using var transaction = new ReminderAdmissionTransaction(CancellationToken.None);
        var budget = new ReminderAcquireBudget(clock, TimeSpan.FromMilliseconds(500));

        var concurrency = await concurrencyGate.AcquireAsync(ReminderDeliveryTestContext.Default(), budget, CancellationToken.None);
        Assert.True(transaction.TryAdd(concurrency.Reservation));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var rate = await rateGate.AcquireAsync(ReminderDeliveryTestContext.Default(), budget, CancellationToken.None);
        Assert.True(transaction.TryAdd(rate.Reservation));

        Assert.Equal(
            ReminderAdmissionCommitOutcome.TimedOut,
            transaction.TryCommit(budget, CancellationToken.None, out _));
        Assert.Equal(1, concurrencyGate.AvailablePermits);
        Assert.Equal(1, rateGate.AvailableTokens);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task RateRollback_DoesNotMintFractionalCapacity()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ReminderThrottleConfigBuilder()
            .PermitsPerSecond(1, 2, ThrottleBlockMode.SkipImmediately)
            .Build();
        using var gate = new LocalRateReminderAdmissionGate(config.Rate!, clock);
        var budget = new ReminderAcquireBudget(clock, timeout: null);

        using var transactionA = new ReminderAdmissionTransaction(CancellationToken.None);
        var reservationA = await gate.AcquireAsync(ReminderDeliveryTestContext.Default(), budget, CancellationToken.None);
        Assert.True(transactionA.TryAdd(reservationA.Reservation));

        clock.Advance(TimeSpan.FromMilliseconds(500));
        using var transactionB = new ReminderAdmissionTransaction(CancellationToken.None);
        var reservationB = await gate.AcquireAsync(ReminderDeliveryTestContext.Default(), budget, CancellationToken.None);
        Assert.True(transactionB.TryAdd(reservationB.Reservation));
        Assert.Equal(
            ReminderAdmissionCommitOutcome.Committed,
            transactionB.TryCommit(budget, CancellationToken.None, out _));

        transactionA.Rollback();

        using var transactionC = new ReminderAdmissionTransaction(CancellationToken.None);
        var reservationC = await gate.AcquireAsync(ReminderDeliveryTestContext.Default(), budget, CancellationToken.None);
        Assert.True(transactionC.TryAdd(reservationC.Reservation));
        Assert.Equal(
            ReminderAdmissionCommitOutcome.Committed,
            transactionC.TryCommit(budget, CancellationToken.None, out _));

        clock.Advance(TimeSpan.FromMilliseconds(500));
        var next = await gate.AcquireAsync(ReminderDeliveryTestContext.Default(), budget, CancellationToken.None);
        Assert.False(next.AdmittedLease);
        Assert.Equal(ReminderSkipReason.LocalLimiterFull, next.SkipReason);
    }
}

public sealed class ThrottleConfigTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public void Builder_RejectsEmptyConfig()
    {
        var b = new ReminderThrottleConfigBuilder();
        Assert.Throws<ArgumentException>(() => b.Build());
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public void Builder_RejectsZeroMaxConcurrent()
    {
        var b = new ReminderThrottleConfigBuilder().MaxConcurrent(0, ThrottleBlockMode.Wait);
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Build());
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public void Builder_RejectsNegativeRate()
    {
        var b = new ReminderThrottleConfigBuilder().PermitsPerSecond(-1.0, 1, ThrottleBlockMode.Wait);
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Build());
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public void Builder_RejectsNonFiniteRate()
    {
        var b = new ReminderThrottleConfigBuilder().PermitsPerSecond(double.PositiveInfinity, 1, ThrottleBlockMode.Wait);
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Build());
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public void Builder_RejectsRateWhoseTokenWaitExceedsRuntimeTimerLimit()
    {
        var b = new ReminderThrottleConfigBuilder().PermitsPerSecond(double.Epsilon, 1, ThrottleBlockMode.Wait);
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Build());
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public void Builder_RejectsNonPositiveBurst()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReminderThrottleConfigBuilder().PermitsPerSecond(10, 0, ThrottleBlockMode.Wait).Build());
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public void Builder_HonorsExplicitBurstSize()
    {
        var c = new ReminderThrottleConfigBuilder().PermitsPerSecond(10, 100, ThrottleBlockMode.Wait).Build();
        Assert.Equal(100, c.BurstSize);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public void Builder_AllowsIndependentLocalLimiterBlockModes()
    {
        var concurrencyMode = ThrottleBlockMode.Wait;
        var rateMode = ThrottleBlockMode.SkipImmediately;

        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(2, concurrencyMode)
            .PermitsPerSecond(5, 7, rateMode)
            .Build();

        Assert.Same(concurrencyMode, config.Concurrency!.BlockMode);
        Assert.Same(rateMode, config.Rate!.BlockMode);
        Assert.Equal(7, config.Rate.BurstSize);
    }
}

public sealed class NoOpThrottleTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task AcquireAsync_AlwaysReturnsSharedAdmittedLease()
    {
        var throttle = NoOpReminderDeliveryThrottle.Instance;
        var ctx = ReminderDeliveryTestContext.Default();

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

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task Dispose_OnNoOpLease_IsIdempotentNoOp()
    {
        var lease = await NoOpReminderDeliveryThrottle.Instance.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None);
        lease.Dispose();
        lease.Dispose();
        // No exception, no state change to verify other than not throwing.
    }
}

public sealed class LocalThrottleConcurrencyTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task MaxConcurrent_BoundsInFlight()
    {
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(2, ThrottleBlockMode.Wait)
            .Build();
        await using var throttle = new TestThrottle(config);

        var l1 = await throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None);
        var l2 = await throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None);
        Assert.Equal(0, throttle.AvailableConcurrencyPermits);

        var l3Task = throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None);
        Assert.False(l3Task.IsCompleted);

        l1.Dispose();
        var l3 = await l3Task.AsTask().WaitAsync(TimeSpan.FromSeconds(2), Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(ReminderAdmissionOutcome.Admitted, l3.Outcome);

        l2.Dispose();
        l3.Dispose();
        Assert.Equal(2, throttle.AvailableConcurrencyPermits);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task SkipImmediately_ReturnsSkippedWhenFull()
    {
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(1, ThrottleBlockMode.SkipImmediately)
            .Build();
        await using var throttle = new TestThrottle(config);

        var l1 = await throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None);
        var l2 = await throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None);

        Assert.Equal(ReminderAdmissionOutcome.Admitted, l1.Outcome);
        Assert.Equal(ReminderAdmissionOutcome.Skipped, l2.Outcome);
        Assert.Equal(ReminderSkipReason.LocalLimiterFull, l2.SkipReason);
        Assert.True(l2.WaitedFor < TimeSpan.FromMilliseconds(50));
        Assert.Equal("test", l2.TierName);

        l1.Dispose();
        l2.Dispose(); // No-op for skipped lease.
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task Cancellation_DoesNotConsumePermit()
    {
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(1, ThrottleBlockMode.Wait)
            .Build();
        await using var throttle = new TestThrottle(config);

        var l1 = await throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var l2Task = throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), cts.Token).AsTask();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => l2Task);

        Assert.Equal(0, throttle.AvailableConcurrencyPermits);
        l1.Dispose();
        Assert.Equal(1, throttle.AvailableConcurrencyPermits);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task PermitIsSpentOnDisposeRegardlessOfCallerOutcome()
    {
        var config = new ReminderThrottleConfigBuilder().MaxConcurrent(1, ThrottleBlockMode.Wait).Build();
        await using var throttle = new TestThrottle(config);

        var lease = await throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None);
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
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task Cancellation_DuringRateWait_ReleasesConcurrencyPermit()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(2, ThrottleBlockMode.Wait)
            .PermitsPerSecond(1, 1, ThrottleBlockMode.Wait)
            .Build();
        await using var throttle = new TestThrottle(config, clock);

        // First acquire takes both the rate token and one concurrency permit.
        var l1 = await throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None);
        Assert.Equal(ReminderAdmissionOutcome.Admitted, l1.Outcome);
        Assert.Equal(1, throttle.AvailableConcurrencyPermits);

        // Second acquire reserves a concurrency permit but blocks on the empty rate bucket.
        using var cts = new CancellationTokenSource();
        var l2Task = throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), cts.Token).AsTask();

        // Yield until the throttle has reserved the second concurrency permit and is waiting on
        // the bucket refill. The acquire completes the semaphore wait synchronously and then awaits
        // Task.Delay against the FakeTimeProvider, which never completes until the clock advances.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (throttle.AvailableConcurrencyPermits == 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Xunit.TestContext.Current.CancellationToken);
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
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task PermitsPerSecond_AdmitsBurstThenSkips()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ReminderThrottleConfigBuilder()
            .PermitsPerSecond(10, 10, ThrottleBlockMode.SkipImmediately)
            .Build();
        await using var throttle = new TestThrottle(config, clock);

        // The configured burst of 10 should be admitted immediately.
        var admitted = 0;
        for (var i = 0; i < 10; i++)
        {
            var l = await throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None);
            if (l.Outcome == ReminderAdmissionOutcome.Admitted)
            {
                admitted++;
            }
            l.Dispose();
        }

        Assert.Equal(10, admitted);

        // 11th immediately is skipped (no tokens left).
        var next = await throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None);
        Assert.Equal(ReminderAdmissionOutcome.Skipped, next.Outcome);
        Assert.Equal(ReminderSkipReason.LocalLimiterFull, next.SkipReason);

        // Advance one second: bucket refills to capacity.
        clock.Advance(TimeSpan.FromSeconds(1));
        var refilled = await throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None);
        Assert.Equal(ReminderAdmissionOutcome.Admitted, refilled.Outcome);
        refilled.Dispose();
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task WaitMode_PacesAcquiresAtConfiguredRate()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ReminderThrottleConfigBuilder()
            .PermitsPerSecond(2, 1, ThrottleBlockMode.Wait)
            .Build();
        await using var throttle = new TestThrottle(config, clock);

        var first = await throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None);
        Assert.Equal(ReminderAdmissionOutcome.Admitted, first.Outcome);
        first.Dispose();

        // Next acquire must wait ~0.5s.
        var nextTask = throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None).AsTask();
        Assert.False(nextTask.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        var next = await nextTask.WaitAsync(TimeSpan.FromSeconds(2), Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(ReminderAdmissionOutcome.Admitted, next.Outcome);
        next.Dispose();
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task WaitUpTo_DoesNotAdmitRateTokenGeneratedAtDeadline()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ReminderThrottleConfigBuilder()
            .PermitsPerSecond(2, 1, ThrottleBlockMode.WaitUpTo(TimeSpan.FromMilliseconds(500)))
            .Build();
        await using var throttle = new TestThrottle(config, clock);

        var first = await throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None);
        var secondTask = throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None).AsTask();

        clock.Advance(TimeSpan.FromMilliseconds(500));
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(ReminderAdmissionOutcome.Skipped, second.Outcome);
        Assert.Equal(ReminderSkipReason.AcquireTimeout, second.SkipReason);
        first.Dispose();
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task ExplicitLimiterBlockModes_AreAppliedIndependently()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(1, ThrottleBlockMode.Wait)
            .PermitsPerSecond(1, 1, ThrottleBlockMode.SkipImmediately)
            .Build();
        await using var throttle = new TestThrottle(config, clock);

        var first = await throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None);
        Assert.Equal(ReminderAdmissionOutcome.Admitted, first.Outcome);

        var secondTask = throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None).AsTask();
        Assert.False(secondTask.IsCompleted);

        first.Dispose();

        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(2), Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(ReminderAdmissionOutcome.Skipped, second.Outcome);
        Assert.Equal(ReminderSkipReason.LocalLimiterFull, second.SkipReason);
    }

    /// <summary>
    /// Regression for adversarial-review finding: WaitUpTo's wait budget must be shared across
    /// sequential waiting gates. Previously, the rate phase started a fresh budget after the
    /// concurrency phase, allowing total wait to exceed the configured timeout.
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task WaitUpTo_BudgetIsSharedAcrossConcurrencyAndRatePhases()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ReminderThrottleConfigBuilder()
            .MaxConcurrent(1, ThrottleBlockMode.WaitUpTo(TimeSpan.FromMilliseconds(800)))
            .PermitsPerSecond(1, 1, ThrottleBlockMode.WaitUpTo(TimeSpan.FromMilliseconds(800)))
            .Build();
        await using var throttle = new TestThrottle(config, clock);

        // First acquire consumes both the rate token and the only concurrency permit.
        var l1 = await throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None);
        Assert.Equal(ReminderAdmissionOutcome.Admitted, l1.Outcome);

        // Second acquire blocks on the semaphore. Advance 500ms (within budget) and then release.
        var l2Task = throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), CancellationToken.None).AsTask();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(l2Task.IsCompleted);

        // Release the first lease — the semaphore is granted. With a shared budget, only ~300ms
        // remain. The rate bucket needs ~500ms to refill its consumed token, so the rate phase
        // must short-circuit to a skip rather than burning a fresh 800ms budget.
        l1.Dispose();

        var l2 = await l2Task.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(ReminderAdmissionOutcome.Skipped, l2.Outcome);
        Assert.Equal(ReminderSkipReason.AcquireTimeout, l2.SkipReason);
        Assert.True(l2.WaitedFor <= TimeSpan.FromMilliseconds(800) + TimeSpan.FromMilliseconds(100),
            $"Total wait exceeded the configured budget: {l2.WaitedFor}");
    }

    /// <summary>
    /// Regression for adversarial-review finding: AcquireAsync must observe a cancellation token
    /// that is already cancelled at entry. Previously the fast-path Wait(0) or bucket-consume
    /// could succeed against a pre-cancelled token, producing an admitted lease the caller
    /// expected to be cancelled.
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task AcquireAsync_ThrowsImmediately_WhenTokenAlreadyCancelled()
    {
        var config = new ReminderThrottleConfigBuilder().MaxConcurrent(1, ThrottleBlockMode.Wait).Build();
        await using var throttle = new TestThrottle(config);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => throttle.AcquireAsync(ReminderDeliveryTestContext.Default(), cts.Token).AsTask());

        Assert.Equal(1, throttle.AvailableConcurrencyPermits);
    }
}

internal sealed class TestThrottle : IAsyncDisposable
{
    private readonly LocalReminderDeliveryThrottle _inner;

    public TestThrottle(
        ThrottleConfig config,
        TimeProvider? timeProvider = null,
        Orleans.Runtime.Messaging.IOverloadDetector? overloadDetector = null,
        bool start = true)
    {
        _inner = new LocalReminderDeliveryThrottle(
            config,
            timeProvider ?? TimeProvider.System,
            tierName: "test",
            overloadDetector,
            startImmediately: false);
        if (start)
        {
            _inner.Start();
        }
    }

    public int AvailableConcurrencyPermits => _inner.AvailableConcurrencyPermits;
    public int AvailableRateTokens => _inner.AvailableRateTokens;
    public int SlowStartCurrentCapacity => _inner.SlowStartCurrentCapacity;

    public void Start() => _inner.Start();

    public void Stop() => _inner.Stop();

    public ValueTask<ReminderDeliveryLease> AcquireAsync(ReminderDeliveryContext ctx, CancellationToken ct)
        => _inner.AcquireAsync(ctx, ct);

    public ValueTask DisposeAsync()
    {
        _inner.Stop();
        _inner.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static class ReminderDeliveryTestContext
{
    public static ReminderDeliveryContext Default()
    {
        var grainId = GrainId.Create("test", "grain");
        var now = DateTime.UtcNow;
        var status = new TickStatus(now, TimeSpan.FromMinutes(1), now);
        return new ReminderDeliveryContext(grainId, "test-reminder", status, now);
    }
}
