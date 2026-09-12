#nullable enable

using System;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Hosting;
using Orleans.Internal;
using Orleans.Reminders;
using Orleans.Reminders.Concurrency;
using Orleans.Runtime;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using ReminderEvents = Orleans.Reminders.Diagnostics.ReminderEvents;

namespace UnitTests.Concurrency;

/// <summary>
/// Cluster-based integration tests for opt-in reminder concurrency control. Uses
/// <see cref="InProcessTestCluster"/> with <see cref="ReminderTestClock"/> so the throttle's
/// token bucket and the reminder schedule share a single deterministic clock.
/// </summary>
[TestSuite("Functional")]
[TestProvider("None")]
[TestArea("Reminders")]
public sealed class ReminderConcurrencyControlClusterTests
{
    /// <summary>
    /// When AddReminderConcurrencyControl is not called, the default DI registration
    /// resolves to the shared no-op throttle.
    /// </summary>
    [Fact]
    public async Task DefaultThrottle_IsNoOp_WhenConcurrencyControlNotConfigured()
    {
        var builder = new InProcessTestClusterBuilder(initialSilosCount: 1);
        builder.AddReminderTestClock();
        builder.ConfigureSilo((_, sb) =>
        {
            sb.AddMemoryGrainStorageAsDefault()
                .AddReminders()
                .UseInMemoryReminderService();
        });

        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        foreach (var silo in cluster.Silos)
        {
            var sp = cluster.GetSiloServiceProvider(silo.SiloAddress);
            var throttle = sp.GetRequiredService<IReminderDeliveryThrottle>();
            Assert.IsType<NoOpReminderDeliveryThrottle>(throttle);
            Assert.Same(NoOpReminderDeliveryThrottle.Instance, throttle);
        }
    }

    /// <summary>
    /// When AddReminderConcurrencyControl is called with a PerSilo tier, the default no-op
    /// throttle is replaced by a configured local throttle.
    /// </summary>
    [Fact]
    public async Task ConfiguredPerSiloThrottle_ReplacesDefaultNoOp()
    {
        var builder = new InProcessTestClusterBuilder(initialSilosCount: 1);
        builder.AddReminderTestClock();
        builder.ConfigureSilo((_, sb) =>
        {
            sb.AddMemoryGrainStorageAsDefault()
                .AddReminders()
                .UseInMemoryReminderService()
                .AddReminderConcurrencyControl(c => c
                    .PerSilo(t => t
                        .MaxConcurrent(4, ThrottleBlockMode.Wait)));
        });

        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        foreach (var silo in cluster.Silos)
        {
            var sp = cluster.GetSiloServiceProvider(silo.SiloAddress);
            var throttle = sp.GetRequiredService<IReminderDeliveryThrottle>();
            var local = Assert.IsType<LocalReminderDeliveryThrottle>(throttle);
            Assert.Equal("per-silo", local.TierName);
            Assert.Equal(4, local.AvailableConcurrencyPermits);
            Assert.Same(
                sp.GetRequiredKeyedService<TimeProvider>(ReminderTimeProviderNames.Reminders),
                local.TimeProvider);
        }
    }

    /// <summary>
    /// AddReminderConcurrencyControl invoked with no tiers configured must fail startup
    /// rather than silently install a no-op.
    /// </summary>
    [Fact]
    public async Task EmptyConcurrencyControlConfiguration_FailsStartup()
    {
        var builder = new InProcessTestClusterBuilder(initialSilosCount: 1);
        builder.AddReminderTestClock();
        builder.ConfigureSilo((_, sb) =>
        {
            sb.AddMemoryGrainStorageAsDefault()
                .AddReminders()
                .UseInMemoryReminderService()
                .AddReminderConcurrencyControl(_ => { /* no tiers configured */ });
        });

        await using var cluster = builder.Build();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => cluster.DeployAsync(TestContext.Current.CancellationToken));
        Assert.Contains("AddReminderConcurrencyControl", FlattenMessages(ex));
    }

    /// <summary>
    /// Reminders fire end-to-end with concurrency control enabled (regression guard).
    /// A PerSilo tier with a permissive limit must not interfere with normal delivery.
    /// </summary>
    [Fact]
    public async Task RemindersStillFire_WithPermissiveConcurrencyControl()
    {
        var builder = new InProcessTestClusterBuilder(initialSilosCount: 1);
        var observer = ReminderDiagnosticObserver.Create(builder);
        using var _o = observer;
        var clock = builder.AddReminderTestClock(minimumReminderPeriod: TimeSpan.FromMilliseconds(100));
        builder.ConfigureSilo((_, sb) =>
        {
            sb.AddMemoryGrainStorageAsDefault()
                .AddReminders()
                .UseInMemoryReminderService()
                .AddReminderConcurrencyControl(c => c
                .PerSilo(t => t.MaxConcurrent(100, ThrottleBlockMode.Wait)));
        });

        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        foreach (var silo in cluster.Silos)
        {
            await observer.WaitForReminderServiceStartedAsync(cts.Token, silo.SiloAddress);
        }

        var grain = cluster.Client.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var grainId = grain.GetGrainId();
        const string reminderName = "test_reminder";
        var period = TimeSpan.FromMilliseconds(500);

        var registered = observer.WaitForReminderRegisteredAsync(grainId, reminderName, cts.Token);
        var handle = await grain.StartReminder(reminderName, period, validate: true).WaitAsync(cts.Token);
        await registered;

        // The reminder's first dueTime is computed inside the test grain and may be 1-2 seconds
        // out from "now". Drive the FakeTimeProvider forward in small steps until the tick fires.
        await observer.WaitForLocalReminderScheduleAsync(grainId, reminderName, cts.Token);
        var tickTask = observer.WaitForReminderTickAsync(grainId, cts.Token, reminderName);

        for (var i = 0; i < 30 && !tickTask.IsCompleted; i++)
        {
            await clock.AdvanceAsync(TimeSpan.FromSeconds(1), cts.Token);
            await Task.Delay(50, cts.Token);
        }

        var tick = await tickTask;
        Assert.Equal(reminderName, tick.ReminderName);

        await grain.StopReminder(handle).WaitAsync(cts.Token);
    }

    /// <summary>
    /// With a tight rate-limit and SkipImmediately block mode, a burst of simultaneously-due
    /// reminders produces TickSkipped events for the dispatches that exceed the bucket
    /// capacity. Verifies the end-to-end wiring of: throttle -&gt; LocalReminderService
    /// dispatch loop -&gt; ReminderEvents diagnostic listener.
    /// </summary>
    [Fact]
    public async Task TickSkipped_EventsFireWhenRateLimitIsExceeded()
    {
        const string reminderName = "burst";
        var skipped = new ConcurrentBag<ReminderEvents.TickSkipped>();
        var completed = new ConcurrentBag<ReminderEvents.TickCompleted>();
        using var subscription = ReminderEvents.AllEvents.Subscribe(evt =>
        {
            switch (evt)
            {
                case ReminderEvents.TickSkipped s when s.ReminderName == reminderName:
                    skipped.Add(s);
                    break;
                case ReminderEvents.TickCompleted c when c.ReminderName == reminderName:
                    completed.Add(c);
                    break;
            }
        });

        var builder = new InProcessTestClusterBuilder(initialSilosCount: 1);
        var observer = ReminderDiagnosticObserver.Create(builder);
        using var _o = observer;
        var clock = builder.AddReminderTestClock(minimumReminderPeriod: TimeSpan.FromMilliseconds(100));
        builder.ConfigureSilo((_, sb) =>
        {
            sb.AddMemoryGrainStorageAsDefault()
                .AddReminders()
                .UseInMemoryReminderService()
                .AddReminderConcurrencyControl(c => c
                    .PerSilo(t => t
                        .PermitsPerSecond(1, 1, ThrottleBlockMode.SkipImmediately)));
        });

        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        foreach (var silo in cluster.Silos)
        {
            await observer.WaitForReminderServiceStartedAsync(cts.Token, silo.SiloAddress);
        }

        const int reminderCount = 6;
        var period = TimeSpan.FromMilliseconds(500);

        // Register all reminders.
        var grains = Enumerable.Range(0, reminderCount)
            .Select(_ => cluster.Client.GetGrain<IReminderTestGrain2>(Guid.NewGuid()))
            .ToArray();

        var handles = new IGrainReminder[reminderCount];
        for (var i = 0; i < reminderCount; i++)
        {
            var g = grains[i];
            var reg = observer.WaitForReminderRegisteredAsync(g.GetGrainId(), reminderName, cts.Token);
            handles[i] = await g.StartReminder(reminderName, period, validate: true).WaitAsync(cts.Token);
            await reg;
            await observer.WaitForLocalReminderScheduleAsync(g.GetGrainId(), reminderName, cts.Token);
        }

        // Drive the FakeTimeProvider forward until at least one tick has been processed (admitted
        // or skipped). Reminders register with an initial due-time around 2s; once we cross that
        // they all become due in the same advance, exhausting the 1-token bucket.
        for (var i = 0; i < 30; i++)
        {
            if (completed.Count >= 1 && skipped.Count >= 1)
            {
                break;
            }

            await clock.AdvanceAsync(TimeSpan.FromSeconds(1), cts.Token);
            await Task.Delay(100, cts.Token);
        }

        try
        {
            Assert.True(completed.Count >= 1, $"Expected at least one TickCompleted, observed {completed.Count}. Skipped={skipped.Count}.");
            Assert.True(skipped.Count >= 1, $"Expected at least one TickSkipped, observed {skipped.Count}. Completed={completed.Count}.");
            var skippedEvent = skipped.First();
            Assert.Equal(ReminderSkipReason.LocalLimiterFull, skippedEvent.Reason);
            Assert.Equal("per-silo", skippedEvent.TierName);
            Assert.Equal(reminderName, skippedEvent.ReminderName);
        }
        finally
        {
            for (var i = 0; i < reminderCount; i++)
            {
                try { await grains[i].StopReminder(handles[i]).WaitAsync(cts.Token); } catch { /* best-effort */ }
            }
        }
    }

    /// <summary>
    /// Regression for adversarial-review finding: when a throttle with WaitMode is configured and
    /// the silo is shut down while one or more reminder ticks are waiting in AcquireAsync, the
    /// shutdown must complete promptly. The previous ordering of StopDeliveringReminders waited
    /// for delivery quiescence BEFORE cancelling the per-reminder cancellation token that
    /// AcquireAsync observes, producing a deadlock.
    /// </summary>
    [Fact]
    public async Task SiloShutdown_CompletesPromptly_WhenThrottleWaitsAreInFlight()
    {
        var blockingThrottle = new BlockingReminderDeliveryThrottle();

        var builder = new InProcessTestClusterBuilder(initialSilosCount: 1);
        var observer = ReminderDiagnosticObserver.Create(builder);
        using var _o = observer;
        var clock = builder.AddReminderTestClock(minimumReminderPeriod: TimeSpan.FromMilliseconds(100));
        builder.ConfigureSilo((_, sb) =>
        {
            sb.AddMemoryGrainStorageAsDefault()
                .AddReminders()
                .UseInMemoryReminderService();
            sb.Services.RemoveAll<IReminderDeliveryThrottle>();
            sb.Services.AddSingleton<IReminderDeliveryThrottle>(blockingThrottle);
        });

        var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        foreach (var silo in cluster.Silos)
        {
            await observer.WaitForReminderServiceStartedAsync(cts.Token, silo.SiloAddress);
        }

        const string reminderName = "shutdown_test";
        var grain = cluster.Client.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var registered = observer.WaitForReminderRegisteredAsync(grain.GetGrainId(), reminderName, cts.Token);
        await grain.StartReminder(reminderName, TimeSpan.Zero, TimeSpan.FromHours(1)).WaitAsync(cts.Token);
        await registered;
        await observer.WaitForLocalReminderScheduleAsync(grain.GetGrainId(), reminderName, cts.Token);

        var acquireStarted = blockingThrottle.WaitForAcquireAsync(cts.Token);
        for (var i = 0; i < 5 && !acquireStarted.IsCompleted; i++)
        {
            await clock.AdvanceAsync(TimeSpan.FromSeconds(1), cts.Token);
        }

        await acquireStarted;

        var shutdownTask = cluster.DisposeAsync().AsTask();
        var completedFirst = await Task.WhenAny(shutdownTask, Task.Delay(TimeSpan.FromSeconds(30), cts.Token));
        Assert.Same(shutdownTask, completedFirst);
        await shutdownTask;
    }

    /// <summary>
    /// Regression for adversarial-review finding: when a tick is skipped by the throttle, no
    /// TickFiring event should be emitted (the grain never observes the tick) and the tardiness
    /// metric should not be recorded for the skipped tick. Verifies the fix that moved
    /// status-construction, TickFiring emission, and tardiness recording after the admit.
    /// </summary>
    [Fact]
    public async Task NoTickFiringEvent_WhenTickIsSkippedByThrottle()
    {
        const string reminderName = "burst2";
        var skipped = new ConcurrentBag<ReminderEvents.TickSkipped>();
        var firings = new ConcurrentBag<ReminderEvents.TickFiring>();
        var tardinessMeasurements = new ConcurrentBag<double>();
        using var subscription = ReminderEvents.AllEvents.Subscribe(evt =>
        {
            switch (evt)
            {
                case ReminderEvents.TickSkipped s when s.ReminderName == reminderName:
                    skipped.Add(s);
                    break;
                case ReminderEvents.TickFiring f when f.ReminderName == reminderName:
                    firings.Add(f);
                    break;
            }
        });

        var builder = new InProcessTestClusterBuilder(initialSilosCount: 1);
        var observer = ReminderDiagnosticObserver.Create(builder);
        using var _o = observer;
        var clock = builder.AddReminderTestClock(minimumReminderPeriod: TimeSpan.FromMilliseconds(100));
        builder.ConfigureSilo((_, sb) =>
        {
            sb.AddMemoryGrainStorageAsDefault()
                .AddReminders()
                .UseInMemoryReminderService()
                .AddReminderConcurrencyControl(c => c
                    .PerSilo(t => t
                        .PermitsPerSecond(1, 1, ThrottleBlockMode.SkipImmediately)));
        });

        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        foreach (var silo in cluster.Silos)
        {
            await observer.WaitForReminderServiceStartedAsync(cts.Token, silo.SiloAddress);
        }

        var targetSilo = Assert.Single(cluster.Silos);
        var orleansInstruments = cluster.GetSiloServiceProvider(targetSilo.SiloAddress).GetRequiredService<OrleansInstruments>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, orleansInstruments.Meter)
                && instrument.Name == "orleans-reminders-tardiness")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<double>((_, measurement, _, _) => tardinessMeasurements.Add(measurement));
        meterListener.Start();

        const int reminderCount = 6;
        var period = TimeSpan.FromHours(1);

        var grains = Enumerable.Range(0, reminderCount)
            .Select(_ => cluster.Client.GetGrain<IReminderTestGrain2>(Guid.NewGuid()))
            .ToArray();

        var handles = new IGrainReminder[reminderCount];
        for (var i = 0; i < reminderCount; i++)
        {
            var g = grains[i];
            var reg = observer.WaitForReminderRegisteredAsync(g.GetGrainId(), reminderName, cts.Token);
            handles[i] = await g.StartReminder(reminderName, TimeSpan.Zero, period).WaitAsync(cts.Token);
            await reg;
            await observer.WaitForLocalReminderScheduleAsync(g.GetGrainId(), reminderName, cts.Token);
        }

        for (var i = 0; i < 30 && skipped.Count + firings.Count < reminderCount; i++)
        {
            await clock.AdvanceAsync(TimeSpan.FromSeconds(1), cts.Token);
            await Task.Delay(100, cts.Token);
        }

        try
        {
            Assert.True(skipped.Count >= 1, $"Expected at least one TickSkipped; got {skipped.Count}.");
            Assert.Equal(reminderCount, skipped.Count + firings.Count);

            foreach (var s in skipped)
            {
                var matching = firings.Where(f =>
                    f.GrainId == s.GrainId &&
                    f.ReminderName == s.ReminderName).ToList();
                Assert.Empty(matching);
            }

            Assert.Equal(firings.Count, tardinessMeasurements.Count);
        }
        finally
        {
            for (var i = 0; i < reminderCount; i++)
            {
                try { await grains[i].StopReminder(handles[i]).WaitAsync(cts.Token); } catch { /* best-effort */ }
            }
        }
    }

    [Fact]
    public async Task SkippedOccurrence_IsNotRetriedBeforeTheNextPeriod()
    {
        const string reminderName = "skip_once";
        var skipped = new ConcurrentBag<ReminderEvents.TickSkipped>();
        var firings = new ConcurrentBag<ReminderEvents.TickFiring>();
        var firstSkipped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = ReminderEvents.AllEvents.Subscribe(evt =>
        {
            switch (evt)
            {
                case ReminderEvents.TickSkipped s when s.ReminderName == reminderName:
                    skipped.Add(s);
                    firstSkipped.TrySetResult();
                    break;
                case ReminderEvents.TickFiring f when f.ReminderName == reminderName:
                    firings.Add(f);
                    firstFiring.TrySetResult();
                    break;
            }
        });

        var throttle = new SkipFirstReminderDeliveryThrottle();

        var builder = new InProcessTestClusterBuilder(initialSilosCount: 1);
        var observer = ReminderDiagnosticObserver.Create(builder);
        using var _o = observer;
        var clock = builder.AddReminderTestClock(minimumReminderPeriod: TimeSpan.FromMilliseconds(100));
        builder.ConfigureSilo((_, sb) =>
        {
            sb.AddMemoryGrainStorageAsDefault()
                .AddReminders()
                .UseInMemoryReminderService();
            sb.Services.RemoveAll<IReminderDeliveryThrottle>();
            sb.Services.AddSingleton<IReminderDeliveryThrottle>(throttle);
        });

        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        foreach (var silo in cluster.Silos)
        {
            await observer.WaitForReminderServiceStartedAsync(cts.Token, silo.SiloAddress);
        }

        var grain = cluster.Client.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var registered = observer.WaitForReminderRegisteredAsync(grain.GetGrainId(), reminderName, cts.Token);
        var period = TimeSpan.FromSeconds(1);
        var handle = await grain.StartReminder(reminderName, period, period).WaitAsync(cts.Token);
        await registered;
        await observer.WaitForLocalReminderScheduleAsync(grain.GetGrainId(), reminderName, cts.Token);

        await clock.AdvanceAsync(period, cts.Token);
        await firstSkipped.Task.WaitAsync(cts.Token);
        Assert.Single(skipped);
        var firstScheduledTick = Assert.Single(throttle.ScheduledTickTimes);

        await Task.Delay(100, cts.Token);
        Assert.Single(skipped);
        Assert.Empty(firings);
        Assert.Equal(1, throttle.AcquireCount);

        await clock.AdvanceAsync(period, cts.Token);
        await firstFiring.Task.WaitAsync(cts.Token);
        Assert.Single(firings);
        Assert.Equal(firstScheduledTick + period, throttle.ScheduledTickTimes.Last());
        await grain.StopReminder(handle).WaitAsync(cts.Token);
    }

    [Fact]
    public async Task StaleSchedule_DoesNotEmitTickSkipped()
    {
        const string reminderName = "stale_skip";
        var skipped = new ConcurrentBag<ReminderEvents.TickSkipped>();
        using var subscription = ReminderEvents.AllEvents.Subscribe(evt =>
        {
            if (evt is ReminderEvents.TickSkipped value && value.ReminderName == reminderName)
            {
                skipped.Add(value);
            }
        });

        var throttle = new DelayedSkipReminderDeliveryThrottle();

        var builder = new InProcessTestClusterBuilder(initialSilosCount: 1);
        var observer = ReminderDiagnosticObserver.Create(builder);
        using var _o = observer;
        var clock = builder.AddReminderTestClock(minimumReminderPeriod: TimeSpan.FromMilliseconds(100));
        builder.ConfigureSilo((_, sb) =>
        {
            sb.AddMemoryGrainStorageAsDefault()
                .AddReminders()
                .UseInMemoryReminderService();
            sb.Services.RemoveAll<IReminderDeliveryThrottle>();
            sb.Services.AddSingleton<IReminderDeliveryThrottle>(throttle);
        });

        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        foreach (var silo in cluster.Silos)
        {
            await observer.WaitForReminderServiceStartedAsync(cts.Token, silo.SiloAddress);
        }

        var grain = cluster.Client.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var registered = observer.WaitForReminderRegisteredAsync(grain.GetGrainId(), reminderName, cts.Token);
        await grain.StartReminder(reminderName, TimeSpan.Zero, TimeSpan.FromHours(1)).WaitAsync(cts.Token);
        await registered;
        await observer.WaitForLocalReminderScheduleAsync(grain.GetGrainId(), reminderName, cts.Token);

        var acquireStarted = throttle.WaitForAcquireAsync(cts.Token);
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(100), cts.Token);
        await acquireStarted;

        var updateTask = grain.StartReminder(reminderName, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        await throttle.WaitForCancellationAsync(cts.Token);
        throttle.Release();
        await updateTask.WaitAsync(cts.Token);

        Assert.Empty(skipped);
        await grain.StopReminder(reminderName).WaitAsync(cts.Token);
    }

    /// <summary>
    /// Regression for Phase 1.5: when a tier opts in to RespectOverload and the silo's
    /// IOverloadDetector reports overload, reminder ticks are skipped (or delayed) per the
    /// configured block mode. Verifies end-to-end wiring through the silo's DI graph using
    /// a replaced IOverloadDetector service.
    /// </summary>
    [Fact]
    public async Task RespectOverload_SkipsTicks_WhenOverloadDetectorReportsOverload()
    {
        const string reminderName = "overload_test";
        var skipped = new ConcurrentBag<ReminderEvents.TickSkipped>();
        using var subscription = ReminderEvents.AllEvents.Subscribe(evt =>
        {
            if (evt is ReminderEvents.TickSkipped s && s.ReminderName == reminderName)
            {
                skipped.Add(s);
            }
        });

        var fakeDetector = new FakeClusterOverloadDetector { IsOverloaded = true };
        var builder = new InProcessTestClusterBuilder(initialSilosCount: 1);
        var observer = ReminderDiagnosticObserver.Create(builder);
        using var _o = observer;
        var clock = builder.AddReminderTestClock(minimumReminderPeriod: TimeSpan.FromMilliseconds(100));
        builder.ConfigureSilo((_, sb) =>
        {
            sb.Services.Replace(ServiceDescriptor.Singleton<Orleans.Runtime.Messaging.IOverloadDetector>(fakeDetector));
            sb.AddMemoryGrainStorageAsDefault()
                .AddReminders()
                .UseInMemoryReminderService()
                .AddReminderConcurrencyControl(c => c
                    .PerSilo(t => t
                        .MaxConcurrent(100, ThrottleBlockMode.Wait)
                        .RespectOverload(ThrottleBlockMode.SkipImmediately)));
        });

        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        foreach (var silo in cluster.Silos)
        {
            await observer.WaitForReminderServiceStartedAsync(cts.Token, silo.SiloAddress);
        }

        var grain = cluster.Client.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var grainId = grain.GetGrainId();
        var registered = observer.WaitForReminderRegisteredAsync(grainId, reminderName, cts.Token);
        var handle = await grain.StartReminder(reminderName, TimeSpan.FromMilliseconds(500), validate: true).WaitAsync(cts.Token);
        await registered;
        await observer.WaitForLocalReminderScheduleAsync(grainId, reminderName, cts.Token);

        // Drive past the first due-time so the reminder fires. With overload reported, the tick
        // must be skipped with reason=SiloOverloaded.
        for (var i = 0; i < 10 && skipped.Count == 0; i++)
        {
            await clock.AdvanceAsync(TimeSpan.FromSeconds(1), cts.Token);
            await Task.Delay(100, cts.Token);
        }

        try
        {
            Assert.True(skipped.Count >= 1, $"Expected at least one TickSkipped(SiloOverloaded); got {skipped.Count}.");
            var skip = skipped.First();
            Assert.Equal(ReminderSkipReason.SiloOverloaded, skip.Reason);
            Assert.Equal("per-silo", skip.TierName);
            Assert.Equal(reminderName, skip.ReminderName);
        }
        finally
        {
            try { await grain.StopReminder(handle).WaitAsync(cts.Token); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Regression for Phase 1.5: slow-start ramp-up reduces the effective concurrency at silo
    /// startup. Verifies that with a tightly constrained slow-start initial capacity, only the
    /// initial capacity admits successfully on the first burst; subsequent ticks are skipped
    /// with SlowStartLimited until the ramp opens up.
    /// </summary>
    [Fact]
    public async Task SlowStart_LimitsInitialFanOut()
    {
        const string reminderName = "slow_start_test";
        var skipped = new ConcurrentBag<ReminderEvents.TickSkipped>();
        using var subscription = ReminderEvents.AllEvents.Subscribe(evt =>
        {
            if (evt is ReminderEvents.TickSkipped s && s.ReminderName == reminderName)
            {
                skipped.Add(s);
            }
        });

        var builder = new InProcessTestClusterBuilder(initialSilosCount: 1);
        var observer = ReminderDiagnosticObserver.Create(builder);
        using var _o = observer;
        var clock = builder.AddReminderTestClock(minimumReminderPeriod: TimeSpan.FromMilliseconds(100));
        builder.ConfigureSilo((_, sb) =>
        {
            sb.AddMemoryGrainStorageAsDefault()
                .AddReminders()
                .UseInMemoryReminderService()
                .AddReminderConcurrencyControl(c => c
                    .PerSilo(t => t
                        .MaxConcurrent(100, ThrottleBlockMode.Wait)
                        .SlowStart(
                            initialCapacity: 1,
                            interval: TimeSpan.FromHours(1), // effectively never grows during the test
                            onCapacityExceeded: ThrottleBlockMode.SkipImmediately)));
        });

        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        foreach (var silo in cluster.Silos)
        {
            await observer.WaitForReminderServiceStartedAsync(cts.Token, silo.SiloAddress);
        }

        const int reminderCount = 5;
        var grains = Enumerable.Range(0, reminderCount)
            .Select(_ => cluster.Client.GetGrain<IReminderTestGrain2>(Guid.NewGuid()))
            .ToArray();

        var handles = new IGrainReminder[reminderCount];
        for (var i = 0; i < reminderCount; i++)
        {
            var g = grains[i];
            var reg = observer.WaitForReminderRegisteredAsync(g.GetGrainId(), reminderName, cts.Token);
            handles[i] = await g.StartReminder(reminderName, TimeSpan.FromMilliseconds(500), validate: true).WaitAsync(cts.Token);
            await reg;
            await observer.WaitForLocalReminderScheduleAsync(g.GetGrainId(), reminderName, cts.Token);
        }

        for (var i = 0; i < 10 && skipped.Count == 0; i++)
        {
            await clock.AdvanceAsync(TimeSpan.FromSeconds(1), cts.Token);
            await Task.Delay(100, cts.Token);
        }

        try
        {
            Assert.True(skipped.Count >= 1, $"Expected at least one TickSkipped(SlowStartLimited); got {skipped.Count}.");
            var skip = skipped.First(s => s.Reason == ReminderSkipReason.SlowStartLimited);
            Assert.Equal("per-silo", skip.TierName);
            Assert.Equal(reminderName, skip.ReminderName);
        }
        finally
        {
            for (var i = 0; i < reminderCount; i++)
            {
                try { await grains[i].StopReminder(handles[i]).WaitAsync(cts.Token); } catch { /* best-effort */ }
            }
        }
    }

    [Fact]
    public async Task SlowStart_RampBeginsAfterInitialReminderLoad()
    {
        var reminderTable = new BlockingInitialReadReminderTable();
        var builder = new InProcessTestClusterBuilder(initialSilosCount: 1);
        var observer = ReminderDiagnosticObserver.Create(builder);
        using var _o = observer;
        var clock = builder.AddReminderTestClock(minimumReminderPeriod: TimeSpan.FromMilliseconds(100));
        builder.ConfigureSilo((_, sb) =>
        {
            sb.AddMemoryGrainStorageAsDefault()
                .AddReminders()
                .AddReminderConcurrencyControl(c => c
                    .PerSilo(t => t
                        .MaxConcurrent(4, ThrottleBlockMode.Wait)
                        .SlowStart(
                            initialCapacity: 1,
                            interval: TimeSpan.FromSeconds(1),
                            onCapacityExceeded: ThrottleBlockMode.SkipImmediately)));
            sb.Services.RemoveAll<IReminderTable>();
            sb.Services.AddSingleton<IReminderTable>(reminderTable);
        });

        await using var cluster = builder.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var deployTask = cluster.DeployAsync(TestContext.Current.CancellationToken);
        await reminderTable.WaitForInitialReadAsync(cts.Token);

        await clock.AdvanceAsync(TimeSpan.FromMinutes(1), cts.Token);

        reminderTable.ReleaseInitialRead();
        await deployTask.WaitAsync(cts.Token);
        var silo = Assert.Single(cluster.Silos);
        await observer.WaitForReminderServiceStartedAsync(cts.Token, silo.SiloAddress);
        var throttle = Assert.IsType<LocalReminderDeliveryThrottle>(
            cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<IReminderDeliveryThrottle>());
        Assert.Equal(1, throttle.SlowStartCurrentCapacity);

        await clock.AdvanceAsync(TimeSpan.FromSeconds(1), cts.Token);
        for (var i = 0; i < 100 && throttle.SlowStartCurrentCapacity != 2; i++)
        {
            await Task.Delay(10, cts.Token);
        }

        Assert.Equal(2, throttle.SlowStartCurrentCapacity);
    }

    private static string FlattenMessages(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            sb.AppendLine(e.Message);
            if (e is AggregateException agg)
            {
                foreach (var inner in agg.Flatten().InnerExceptions)
                {
                    sb.AppendLine(inner.Message);
                }
            }
        }

        return sb.ToString();
    }
}

internal sealed class FakeClusterOverloadDetector : Orleans.Runtime.Messaging.IOverloadDetector
{
    public bool IsOverloaded { get; set; }
}

internal sealed class BlockingReminderDeliveryThrottle : IReminderDeliveryThrottle
{
    private readonly TaskCompletionSource _acquireStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitForAcquireAsync(CancellationToken cancellationToken) => _acquireStarted.Task.WaitAsync(cancellationToken);

    public async ValueTask<ReminderDeliveryLease> AcquireAsync(ReminderDeliveryContext context, CancellationToken cancellationToken)
    {
        _acquireStarted.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return ReminderDeliveryLease.NoOpAdmitted;
    }
}

internal sealed class SkipFirstReminderDeliveryThrottle : IReminderDeliveryThrottle
{
    private int _acquireCount;
    private readonly ConcurrentQueue<DateTime> _scheduledTickTimes = new();

    public int AcquireCount => Volatile.Read(ref _acquireCount);

    public IReadOnlyCollection<DateTime> ScheduledTickTimes => _scheduledTickTimes.ToArray();

    public ValueTask<ReminderDeliveryLease> AcquireAsync(ReminderDeliveryContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _scheduledTickTimes.Enqueue(context.ScheduledTickTime);
        return Interlocked.Increment(ref _acquireCount) == 1
            ? ValueTask.FromResult(ReminderDeliveryLease.Skipped("test", TimeSpan.Zero, ReminderSkipReason.LocalLimiterFull))
            : ValueTask.FromResult(ReminderDeliveryLease.NoOpAdmitted);
    }
}

internal sealed class DelayedSkipReminderDeliveryThrottle : IReminderDeliveryThrottle
{
    private readonly TaskCompletionSource _acquireStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitForAcquireAsync(CancellationToken cancellationToken) => _acquireStarted.Task.WaitAsync(cancellationToken);

    public Task WaitForCancellationAsync(CancellationToken cancellationToken) => _cancelled.Task.WaitAsync(cancellationToken);

    public void Release() => _release.TrySetResult();

    public async ValueTask<ReminderDeliveryLease> AcquireAsync(ReminderDeliveryContext context, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), _cancelled);
        _acquireStarted.TrySetResult();
        await _release.Task;
        return ReminderDeliveryLease.Skipped("test", TimeSpan.Zero, ReminderSkipReason.LocalLimiterFull);
    }
}

internal sealed class BlockingInitialReadReminderTable : IReminderTable
{
    private readonly TaskCompletionSource _initialReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseInitialRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitForInitialReadAsync(CancellationToken cancellationToken) => _initialReadStarted.Task.WaitAsync(cancellationToken);

    public void ReleaseInitialRead() => _releaseInitialRead.TrySetResult();

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<ReminderTableData> ReadRows(uint begin, uint end)
    {
        _initialReadStarted.TrySetResult();
        await _releaseInitialRead.Task;
        return new ReminderTableData();
    }

    public Task<ReminderTableData> ReadRows(GrainId grainId) => Task.FromResult(new ReminderTableData());

    public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName) => Task.FromResult<ReminderEntry?>(null);

    public Task<string?> UpsertRow(ReminderEntry entry) => Task.FromResult<string?>(Guid.NewGuid().ToString());

    public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) => Task.FromResult(false);

    public Task TestOnlyClearTable() => Task.CompletedTask;
}
