using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Xunit;
using KeyNotification = Orleans.Runtime.Dissemination.DisseminationBroadcastQueue.KeyNotification;

namespace UnitTests.Dissemination;

[TestCategory("BVT"), TestCategory("Dissemination")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Dissemination")]
public class DisseminationRootBatcherTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;
    private static TimeSpan Milliseconds(int value) => TimeSpan.FromMilliseconds(value);

    [Fact]
    public async Task IdleRootCollectsFor25MillisecondsAndReturnsToIdle()
    {
        await using var rig = new TestRig();
        rig.Clock.Advance(TimeSpan.FromHours(1));
        var start = rig.Clock.GetTimestamp();

        Assert.Equal(Milliseconds(25), await rig.NotifyAsync(new KeyNotification("a", 1, true)));
        rig.Clock.Advance(Milliseconds(24));
        Assert.Empty(rig.Attempts);
        var attempt = await rig.AdvanceToDispatchAsync(Milliseconds(1));
        Assert.Equal(new KeyNotification("a", 1, true), Assert.Single(attempt.Notifications));
        Assert.Equal(Milliseconds(25), rig.Clock.GetElapsedTime(start, attempt.Timestamp));

        rig.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Single(rig.Attempts);
        Assert.Equal(1, rig.Clock.TimerCount);
        Assert.Empty(rig.Namespace.VersionReads);
    }

    [Fact]
    public async Task SustainedUpdatesDispatchAt200MillisecondIntervals()
    {
        await using var rig = new TestRig();
        Assert.Equal(Milliseconds(25), await rig.NotifyAsync(new KeyNotification("a", 1, false)));
        var previous = await rig.AdvanceToDispatchAsync(Milliseconds(25));

        for (var version = 2; version <= 5; version++)
        {
            Assert.Equal(Milliseconds(200), await rig.NotifyAsync(new KeyNotification("a", version, false)));
            rig.Clock.Advance(Milliseconds(199));
            Assert.Equal(version - 1, rig.Attempts.Count);
            var current = await rig.AdvanceToDispatchAsync(Milliseconds(1));
            Assert.Equal(Milliseconds(200), rig.Clock.GetElapsedTime(previous.Timestamp, current.Timestamp));
            Assert.Equal(version, Assert.Single(current.Notifications).Version);
            previous = current;
        }
    }

    [Fact]
    public async Task CollectionWindowCanOutliveLastDispatchInterval()
    {
        await using var rig = new TestRig();
        await rig.NotifyAsync(new KeyNotification("a", 1, false));
        var first = await rig.AdvanceToDispatchAsync(Milliseconds(25));
        rig.Clock.Advance(Milliseconds(190));
        Assert.Equal(Milliseconds(25), await rig.NotifyAsync(new KeyNotification("a", 2, false)));
        rig.Clock.Advance(Milliseconds(24));
        Assert.Single(rig.Attempts);

        var second = await rig.AdvanceToDispatchAsync(Milliseconds(1));
        Assert.Equal(Milliseconds(215), rig.Clock.GetElapsedTime(first.Timestamp, second.Timestamp));
    }

    [Fact]
    public async Task StaggeredAndBurstUpdatesMergeLatestValuesWithoutPushingDeadline()
    {
        await using var rig = new TestRig();
        Assert.Equal(Milliseconds(25), await rig.NotifyAsync(new KeyNotification("a", 1, true)));
        rig.Clock.Advance(Milliseconds(10));
        Assert.True(rig.Batcher.Notify([new("b", 3, false), new("a", 2, false)]));
        rig.Clock.Advance(Milliseconds(10));
        Assert.True(rig.Batcher.Notify([new("a", 9, false), new("a", 4, false)]));
        Assert.Empty(rig.Attempts);
        Assert.Equal(1, rig.Clock.ScheduleCount);

        var attempt = await rig.AdvanceToDispatchAsync(Milliseconds(5));
        Assert.Equal(
            new KeyNotification[] { new("a", 9, true), new("b", 3, false) },
            attempt.Notifications);
    }

    [Fact]
    public async Task LateWakeDoesNotAccumulateCreditsOrProduceCatchupBurst()
    {
        await using var rig = new TestRig();
        rig.Options.MaxBatchItems = 1;
        await rig.NotifyAsync(new("a", 1, false), new("b", 1, false), new("c", 1, false));

        var first = await rig.AdvanceToDispatchAsync(TimeSpan.FromSeconds(5), morePending: true);
        Assert.Single(rig.Attempts);
        var second = await rig.AdvanceToDispatchAsync(Milliseconds(200), morePending: true);
        Assert.Equal(2, rig.Attempts.Count);
        Assert.Equal(Milliseconds(200), rig.Clock.GetElapsedTime(first.Timestamp, second.Timestamp));
        var third = await rig.AdvanceToDispatchAsync(Milliseconds(200));
        Assert.Equal(Milliseconds(200), rig.Clock.GetElapsedTime(second.Timestamp, third.Timestamp));
    }

    [Fact]
    public async Task TwoThousandKeysUseBoundedFairChunksAndRetainedKeysUpdateAtCapacity()
    {
        await using var rig = new TestRig();
        rig.Options.MaxBatchItems = 128;
        rig.Namespace.Options.MaxPendingItemCount = 2000;
        var notifications = Enumerable.Range(0, 2001)
            .Select(static index => new KeyNotification($"key-{index}", 1, false)).ToArray();
        var scheduled = rig.Clock.WhenScheduled();
        Assert.False(rig.Batcher.Notify(notifications));
        Assert.Equal(Milliseconds(25), await Phase(scheduled, "bounded inventory collection"));
        Assert.True(rig.Batcher.Notify([new("key-0", 9, false), new("key-1999", 7, false)]));
        Assert.False(rig.Batcher.Notify([new("overflow", 1, false)]));

        var first = await rig.AdvanceToDispatchAsync(Milliseconds(25), morePending: true);
        Assert.Equal(128, first.Notifications.Length);
        Assert.Equal(9, first.Notifications[0].Version);

        // Updating a waiting key must not move it behind later keys. A dispatched key can be
        // re-admitted, but joins the tail instead of perpetually jumping ahead of the old inventory.
        Assert.True(rig.Batcher.Notify(
            [new("key-128", 6, false), new("key-0", 10, false), new("overflow", 1, false)]));
        for (var wave = 1; wave < 16; wave++)
        {
            var attempt = await rig.AdvanceToDispatchAsync(Milliseconds(200), morePending: wave < 15);
            Assert.Equal(wave < 15 ? 128 : 82, attempt.Notifications.Length);
        }

        var sent = rig.Attempts.SelectMany(static attempt => attempt.Notifications).ToArray();
        Assert.Equal(
            notifications.Take(2000).Select(static notification => notification.Key)
                .Concat(new DisseminationKey[] { "key-0", "overflow" }),
            sent.Select(static notification => notification.Key));
        Assert.Equal(6, sent[128].Version);
        Assert.Equal(7, sent[1999].Version);
        Assert.Equal(new KeyNotification("key-0", 10, false), sent[2000]);
        Assert.DoesNotContain(sent, static notification => notification.Key.Equals(new DisseminationKey("key-2000")));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task UpdatesDuringDispatchPreserveGenerationForceAndInFlightCapacity(bool firstAccepted, bool newForce)
    {
        await using var rig = new TestRig();
        rig.Namespace.Options.MaxPendingItemCount = 1;
        using var release = new ManualResetEventSlim();
        var cancellationToken = TestToken;
        rig.OnDispatch = attempt =>
        {
            if (attempt.Number == 1)
            {
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), cancellationToken), "First dispatch was not released.");
            }

            return attempt.Number > 1 || firstAccepted;
        };

        await rig.NotifyAsync(new KeyNotification("a", 1, true));
        var nextSchedule = rig.Clock.WhenScheduled();
        rig.Clock.Advance(Milliseconds(25));
        var first = await rig.NextAttemptAsync();
        try
        {
            // Another thread must be able to enter Notify while the synchronous callback is blocked.
            // Re-entering on the callback's thread alone would not prove the lock was released.
            await Phase(Task.Run(() =>
            {
                Assert.False(rig.Batcher.Notify([new("b", 1, true)]));
                Assert.True(rig.Batcher.Notify([new("a", 2, newForce)]));
            }, TestToken), "concurrent admission while callback owns the first snapshot");
            Assert.Equal(new KeyNotification("a", 1, true), Assert.Single(first.Notifications));
        }
        finally
        {
            release.Set();
        }

        Assert.Equal(Milliseconds(200), await Phase(nextSchedule, "new generation scheduled after dispatch"));
        var second = await rig.AdvanceToDispatchAsync(Milliseconds(200));
        Assert.Equal(new KeyNotification("a", 2, newForce), Assert.Single(second.Notifications));
        Assert.Equal(2, rig.Attempts.Count);
    }

    [Theory]
    [InlineData(false, 1L)]
    [InlineData(true, 1L)]
    [InlineData(true, 2L)]
    public async Task PartialAdmissionRetriesPreserveOriginalAndNewForce(bool newForce, long version)
    {
        await using var rig = new TestRig();
        var firstPeerVersions = new Dictionary<DisseminationKey, long>();
        var firstPeerSends = new List<KeyNotification>();
        var secondPeerSends = new List<KeyNotification>();
        rig.OnDispatch = attempt =>
        {
            foreach (var notification in attempt.Notifications)
            {
                if (notification.Force
                    || !firstPeerVersions.TryGetValue(notification.Key, out var knownVersion)
                    || notification.Version > knownVersion)
                {
                    firstPeerVersions[notification.Key] = notification.Version;
                    firstPeerSends.Add(notification);
                }
            }

            if (attempt.Number == 1)
            {
                return false;
            }

            secondPeerSends.AddRange(attempt.Notifications);
            return true;
        };
        await rig.NotifyAsync(new("a", 1, true), new("b", 1, true));
        await rig.AdvanceToDispatchAsync(Milliseconds(25), morePending: true);
        if (newForce)
        {
            Assert.True(rig.Batcher.Notify([new("a", version, true)]));
        }

        var retried = await rig.AdvanceToDispatchAsync(Milliseconds(200));
        Assert.Equal(
            new KeyNotification[] { new("a", version, true), new("b", 1, true) },
            retried.Notifications);
        Assert.Equal(
            new KeyNotification[] { new("a", 1, true), new("b", 1, true), new("a", version, true), new("b", 1, true) },
            firstPeerSends);
        Assert.Equal(retried.Notifications, secondPeerSends);
    }

    [Fact]
    public async Task PartialRetryRotatesBehindWaitingKeys()
    {
        await using var rig = new TestRig();
        rig.Options.MaxBatchItems = 1;
        rig.OnDispatch = attempt => attempt.Number != 1;
        await rig.NotifyAsync(new("a", 1, true), new("b", 1, true), new("c", 1, true));
        await rig.AdvanceToDispatchAsync(Milliseconds(25), morePending: true);
        await rig.AdvanceToDispatchAsync(Milliseconds(200), morePending: true);
        await rig.AdvanceToDispatchAsync(Milliseconds(200), morePending: true);
        await rig.AdvanceToDispatchAsync(Milliseconds(200));

        Assert.Equal(
            new KeyNotification[] { new("a", 1, true), new("b", 1, true), new("c", 1, true), new("a", 1, true) },
            rig.Attempts.SelectMany(static attempt => attempt.Notifications));
    }

    [Fact]
    public async Task CallbackExceptionRetriesAndReportsFailure()
    {
        await using var rig = new TestRig();
        var failure = new InvalidOperationException("The parent dispatcher failed.");
        rig.OnDispatch = attempt => attempt.Number == 1 ? throw failure : true;
        await rig.NotifyAsync(new KeyNotification("a", 1, true));

        await rig.AdvanceToDispatchAsync(Milliseconds(25), morePending: true);
        var entry = Assert.Single(rig.Logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(failure, entry.Exception);
        var retry = await rig.AdvanceToDispatchAsync(Milliseconds(200));
        Assert.Equal(new KeyNotification("a", 1, true), Assert.Single(retry.Notifications));
    }

    [Fact]
    public async Task CallbackLoggingFailureIsVisibleToAdmissionAndDrain()
    {
        await using var rig = new TestRig();
        var dispatchFailure = new InvalidOperationException("The parent dispatcher failed.");
        rig.Logger.ThrowOnLog = true;
        rig.OnDispatch = _ => throw dispatchFailure;
        await rig.NotifyAsync(new KeyNotification("a", 1, true));
        rig.Clock.Advance(Milliseconds(25));
        var failure = await Phase(rig.Logger.WorkerFailed.Task, "dispatch logging failure recorded");
        var aggregate = Assert.IsType<AggregateException>(failure);
        Assert.Same(dispatchFailure, aggregate.InnerExceptions[0]);

        var admission = Assert.Throws<InvalidOperationException>(() => rig.Batcher.Notify([new("b", 1, true)]));
        Assert.Same(failure, admission.InnerException);
        var drain = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Batcher.FlushAsync(TestToken));
        Assert.Same(failure, drain.InnerException);
    }

    [Fact]
    public async Task WorkerFailureIsObservedByActiveAndLaterFlushNotifyAndStop()
    {
        await using var rig = new TestRig();
        using var release = new ManualResetEventSlim();
        var cancellationToken = TestToken;
        rig.Logger.ThrowOnLog = true;
        rig.OnDispatch = _ =>
        {
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), cancellationToken), "Failing dispatch was not released.");
            return false;
        };
        await rig.NotifyAsync(new KeyNotification("a", 1, true));
        rig.Clock.Advance(Milliseconds(25));
        await rig.NextAttemptAsync();
        Task activeFlush;
        try
        {
            activeFlush = rig.Batcher.FlushAsync(TestToken);
            Assert.False(activeFlush.IsCompleted);
            rig.Clock.FailNextSchedule = true;
        }
        finally
        {
            release.Set();
        }

        var failure = await Phase(rig.Logger.WorkerFailed.Task, "terminal timer failure logged");
        var activeFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Phase(activeFlush, "active flush observes terminal failure"));
        Assert.Same(failure, activeFailure.InnerException);
        var notificationFailure = Assert.Throws<InvalidOperationException>(
            () => rig.Batcher.Notify([new("b", 2, false)]));
        Assert.Same(failure, notificationFailure.InnerException);
        var flushFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Batcher.FlushAsync(TestToken));
        Assert.Same(failure, flushFailure.InnerException);
        var stopFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Batcher.StopAsync(TestToken));
        Assert.Same(failure, stopFailure.InnerException);
        Assert.True(rig.Clock.TimerDisposed);
        Assert.Single(rig.Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisablingDropsPendingHintsAndReenableDoesNotReplay(bool global)
    {
        await using var rig = new TestRig();
        await rig.NotifyAsync(new KeyNotification("old", 1, true));
        if (global)
        {
            rig.Options.Enabled = false;
        }
        else
        {
            rig.Namespace.Options.Enabled = false;
        }

        await Phase(rig.Batcher.FlushAsync(TestToken), "disabled hints retired");
        Assert.False(rig.Batcher.Notify([new("disabled", 2, true)]));
        Assert.Empty(rig.Attempts);
        rig.Options.Enabled = true;
        rig.Namespace.Options.Enabled = true;
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Milliseconds(25), await rig.NotifyAsync(new KeyNotification("new", 3, false)));

        var sent = await rig.AdvanceToDispatchAsync(Milliseconds(25));
        Assert.Equal(new KeyNotification("new", 3, false), Assert.Single(sent.Notifications));
        Assert.Single(rig.Attempts);
    }

    [Fact]
    public async Task PruneRemovesRetiredKeysAndFreesAdmissionCapacity()
    {
        await using var rig = new TestRig();
        rig.Namespace.Options.MaxPendingItemCount = 2;
        await rig.NotifyAsync(new("retired", 1, true), new("retained", 2, false));
        var scheduled = rig.Clock.WhenScheduled();
        rig.Batcher.Prune(new HashSet<DisseminationKey> { "retained" });
        Assert.True(rig.Batcher.Notify([new("new", 3, false)]));
        Assert.Equal(Milliseconds(25), await Phase(scheduled, "pruned inventory rescheduled"));

        var attempt = await rig.AdvanceToDispatchAsync(Milliseconds(25));
        Assert.Equal(
            new KeyNotification[] { new("retained", 2, false), new("new", 3, false) },
            attempt.Notifications);
    }

    [Fact]
    public async Task PrunePreservesLiveKeysPublishedAfterInventoryCapture()
    {
        await using var rig = new TestRig();
        await rig.NotifyAsync(new("retired", 1, false), new("retained", 2, false));
        var inventory = new HashSet<DisseminationKey> { "retained" };
        rig.Namespace.GetVersionHandler = key => key.Equals(new DisseminationKey("new")) ? 3 : 0;
        Assert.True(rig.Batcher.Notify([new("new", 3, false)]));
        var scheduled = rig.Clock.WhenScheduled();

        rig.Batcher.Prune(inventory);

        Assert.Equal(Milliseconds(25), await Phase(scheduled, "fresh owner versions checked before pruning"));
        var attempt = await rig.AdvanceToDispatchAsync(Milliseconds(25));
        Assert.Equal(
            new KeyNotification[] { new("retained", 2, false), new("new", 3, false) },
            attempt.Notifications);
        Assert.Equal(new DisseminationKey[] { "retired", "new" }, rig.Namespace.VersionReads);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(2L)]
    public async Task PruneVersionReadRunsOutsideLockAndPreservesRacingNotifications(long version)
    {
        await using var rig = new TestRig();
        await rig.NotifyAsync(new KeyNotification("a", 1, false));
        using var release = new ManualResetEventSlim();
        var cancellationToken = TestToken;
        var versionRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Namespace.GetVersionHandler = key =>
        {
            Assert.Equal(new DisseminationKey("a"), key);
            versionRead.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), cancellationToken), "Owner version read was not released.");
            return 0;
        };
        var scheduled = rig.Clock.WhenScheduled();
        var prune = Task.Run(() => rig.Batcher.Prune(new HashSet<DisseminationKey>()), cancellationToken);
        try
        {
            await Phase(versionRead.Task, "pruning captured the old admission generation");
            await Phase(Task.Run(
                () => Assert.True(rig.Batcher.Notify([new("a", version, false)])), cancellationToken),
                "notification admitted while the owner version read is blocked");
        }
        finally
        {
            release.Set();
            await Phase(prune, "pruning reconciles the captured generation");
        }

        Assert.Equal(Milliseconds(25), await Phase(scheduled, "racing notification remains scheduled"));
        var attempt = await rig.AdvanceToDispatchAsync(Milliseconds(25));
        Assert.Equal(new KeyNotification("a", version, false), Assert.Single(attempt.Notifications));
    }

    [Fact]
    public async Task PruneDuringDispatchDoesNotEraseReadmittedKey()
    {
        await using var rig = new TestRig();
        rig.Namespace.Options.MaxPendingItemCount = 1;
        using var release = new ManualResetEventSlim();
        var cancellationToken = TestToken;
        rig.OnDispatch = attempt =>
        {
            if (attempt.Number == 1)
            {
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), cancellationToken), "Pruned snapshot was not released.");
            }

            return true;
        };
        await rig.NotifyAsync(new KeyNotification("a", 1, true));
        var nextSchedule = rig.Clock.WhenScheduled();
        rig.Clock.Advance(Milliseconds(25));
        await rig.NextAttemptAsync();
        try
        {
            rig.Batcher.Prune(new HashSet<DisseminationKey>());
            Assert.True(rig.Batcher.Notify([new("a", 7, true)]));
        }
        finally
        {
            release.Set();
        }

        Assert.Equal(Milliseconds(200), await Phase(nextSchedule, "readmitted identity scheduled"));
        var next = await rig.AdvanceToDispatchAsync(Milliseconds(200));
        Assert.Equal(new KeyNotification("a", 7, true), Assert.Single(next.Notifications));
    }

    [Fact]
    public async Task HighPriorityBypassesCollectionAndCadenceButRetriesRemainPaced()
    {
        await using var rig = new TestRig();
        rig.Namespace.Options.Priority = DisseminationPriority.High;
        rig.OnDispatch = attempt => attempt.Number != 1;
        var start = rig.Clock.GetTimestamp();
        var retryScheduled = rig.Clock.WhenScheduled();
        Assert.True(rig.Batcher.Notify([new("a", 1, true)]));
        var first = await rig.NextAttemptAsync();
        Assert.Equal(start, first.Timestamp);
        Assert.Equal(Milliseconds(200), await Phase(retryScheduled, "high-priority retry paced"));
        await rig.AdvanceToDispatchAsync(Milliseconds(200));

        Assert.True(rig.Batcher.Notify([new("b", 2, true)]));
        var next = await rig.NextAttemptAsync();
        await Phase(rig.Batcher.FlushAsync(TestToken), "high-priority handoff completes");
        Assert.Equal(rig.Clock.GetTimestamp(), next.Timestamp);
        Assert.Equal(new KeyNotification("b", 2, true), Assert.Single(next.Notifications));
    }

    [Fact]
    public async Task PruneReevaluatesCurrentRateWithoutGrantingCredits()
    {
        await using var rig = new TestRig();
        await rig.NotifyAsync(new KeyNotification("a", 1, false));
        var first = await rig.AdvanceToDispatchAsync(Milliseconds(25));
        await rig.NotifyAsync(new KeyNotification("b", 1, false));
        rig.Clock.Advance(Milliseconds(20));
        rig.Options.Overlay.AggregationBroadcastsPerSecond = 10;
        var scheduled = rig.Clock.WhenScheduled();
        rig.Batcher.Prune(new HashSet<DisseminationKey> { "b" });
        Assert.Equal(Milliseconds(80), await Phase(scheduled, "updated rate applied to existing deadline"));
        rig.Clock.Advance(Milliseconds(79));
        Assert.Single(rig.Attempts);
        var second = await rig.AdvanceToDispatchAsync(Milliseconds(1));
        Assert.Equal(Milliseconds(100), rig.Clock.GetElapsedTime(first.Timestamp, second.Timestamp));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembershipWakeForceHandsOffPendingChunksWithoutCredits(bool previousDispatch)
    {
        await using var rig = new TestRig();
        var start = rig.Clock.GetTimestamp();
        rig.Options.MaxBatchItems = 2;
        if (previousDispatch)
        {
            await rig.NotifyAsync(new KeyNotification("seed", 1, false));
            await rig.AdvanceToDispatchAsync(Milliseconds(25));
        }

        await rig.NotifyAsync(
            new("a", 2, true), new("b", 3, true), new("c", 4, true), new("d", 5, true), new("e", 6, true));
        var handoffTime = rig.Clock.GetTimestamp();

        rig.Batcher.WakeForMembershipChange();

        // Await every callback before calling Flush: otherwise Flush could conceal a missing
        // membership wake or failure to bypass collection/pacing for later identity chunks.
        var handoffs = new[] { await rig.NextAttemptAsync(), await rig.NextAttemptAsync(), await rig.NextAttemptAsync() };
        await Phase(rig.Batcher.FlushAsync(TestToken), "membership handoff reconciled");
        Assert.Equal(new[] { 2, 2, 1 }, handoffs.Select(static attempt => attempt.Notifications.Length));
        Assert.Equal(new long[] { 2, 3, 4, 5, 6 }, handoffs.SelectMany(static attempt => attempt.Notifications).Select(static n => n.Version));
        Assert.All(handoffs, attempt => Assert.Equal(handoffTime, attempt.Timestamp));
        Assert.Equal(previousDispatch ? Milliseconds(25) : TimeSpan.Zero, rig.Clock.GetElapsedTime(start, handoffTime));

        Assert.Equal(Milliseconds(200), await rig.NotifyAsync(new KeyNotification("later", 7, false)));
        rig.Clock.Advance(Milliseconds(199));
        Assert.Equal(previousDispatch ? 4 : 3, rig.Attempts.Count);
        var later = await rig.AdvanceToDispatchAsync(Milliseconds(1));
        Assert.Equal(new KeyNotification("later", 7, false), Assert.Single(later.Notifications));
    }

    [Fact]
    public async Task MembershipWakeKeepsPartialHandoffRetriesPaced()
    {
        await using var rig = new TestRig();
        rig.OnDispatch = attempt => attempt.Number != 1;
        await rig.NotifyAsync(new KeyNotification("a", 1, true));
        var first = await rig.AdvanceToDispatchAsync(Milliseconds(25), morePending: true);
        rig.Clock.Advance(Milliseconds(20));
        var scheduled = rig.Clock.WhenScheduled();

        rig.Batcher.WakeForMembershipChange();

        Assert.Equal(Milliseconds(180), await Phase(scheduled, "membership handoff preserves the retry floor"));
        rig.Clock.Advance(Milliseconds(179));
        Assert.Single(rig.Attempts);
        var retry = await rig.AdvanceToDispatchAsync(Milliseconds(1));
        Assert.Equal(Milliseconds(200), rig.Clock.GetElapsedTime(first.Timestamp, retry.Timestamp));
        Assert.Equal(new KeyNotification("a", 1, true), Assert.Single(retry.Notifications));
    }

    [Fact]
    public async Task StopRejectsAdmissionAndForceDrainsBoundedChunksWithoutAdvancingTime()
    {
        await using var rig = new TestRig();
        rig.Options.MaxBatchItems = 2;
        await rig.NotifyAsync(
            new("a", 1, true), new("b", 2, true), new("c", 3, true), new("d", 4, true), new("e", 5, true));
        var start = rig.Clock.GetTimestamp();

        await Phase(rig.Batcher.StopAsync(TestToken), "accepted keys handed to peer queues");

        Assert.False(rig.Batcher.Notify([new("late", 6, true)]));
        Assert.Equal(new[] { 2, 2, 1 }, rig.Attempts.Select(static attempt => attempt.Notifications.Length));
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, rig.Attempts.SelectMany(static attempt => attempt.Notifications).Select(static n => n.Version));
        Assert.All(rig.Attempts, attempt => Assert.Equal(start, attempt.Timestamp));
        Assert.True(rig.Clock.TimerDisposed);
        await rig.Batcher.StopAsync(TestToken);
    }

    [Fact]
    public async Task FlushForcesBoundedChunksWithoutStoppingAdmission()
    {
        await using var rig = new TestRig();
        rig.Options.MaxBatchItems = 2;
        await rig.NotifyAsync(new("a", 1, true), new("b", 2, true), new("c", 3, true));
        var start = rig.Clock.GetTimestamp();

        await Phase(rig.Batcher.FlushAsync(TestToken), "forced chunks handed to peer queues");

        Assert.Equal(new[] { 2, 1 }, rig.Attempts.Select(static attempt => attempt.Notifications.Length));
        Assert.All(rig.Attempts, attempt => Assert.Equal(start, attempt.Timestamp));
        // Consume the two forced callbacks before awaiting the later, ordinarily paced callback.
        await rig.NextAttemptAsync();
        await rig.NextAttemptAsync();
        Assert.Equal(Milliseconds(200), await rig.NotifyAsync(new KeyNotification("later", 4, true)));
        var next = await rig.AdvanceToDispatchAsync(Milliseconds(200));
        Assert.Equal(new KeyNotification("later", 4, true), Assert.Single(next.Notifications));
    }

    [Fact]
    public async Task StopCancellationEndsRetryBudgetAndPreservesCallerToken()
    {
        await using var rig = new TestRig();
        using var cancellation = new CancellationTokenSource();
        rig.OnDispatch = _ => false;
        await rig.NotifyAsync(new KeyNotification("a", 1, true));
        var retryScheduled = rig.Clock.WhenScheduled();
        var stop = rig.Batcher.StopAsync(cancellation.Token);
        await rig.NextAttemptAsync();
        Assert.Equal(Milliseconds(200), await Phase(retryScheduled, "forced stop retry remains paced"));
        Assert.False(stop.IsCompleted);
        Assert.False(rig.Batcher.Notify([new("late", 2, true)]));

        cancellation.Cancel();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Phase(stop, "stop caller budget observed"));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        var laterFailure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.Batcher.StopAsync(CancellationToken.None));
        Assert.Equal(cancellation.Token, laterFailure.CancellationToken);
        Assert.True(rig.Clock.TimerDisposed);
        rig.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Single(rig.Attempts);
    }

    [Fact]
    public async Task PreCanceledStopRejectsAdmissionWithoutDispatch()
    {
        await using var rig = new TestRig();
        using var cancellation = new CancellationTokenSource();
        await rig.NotifyAsync(new KeyNotification("a", 1, true));
        cancellation.Cancel();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.Batcher.StopAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.False(rig.Batcher.Notify([new("b", 2, true)]));
        Assert.Empty(rig.Attempts);
        Assert.True(rig.Clock.TimerDisposed);
    }

    [Fact]
    public async Task FlushCancellationLeavesAcceptedWorkAvailableForPacedRetry()
    {
        await using var rig = new TestRig();
        using var cancellation = new CancellationTokenSource();
        rig.OnDispatch = attempt => attempt.Number != 1;
        await rig.NotifyAsync(new KeyNotification("a", 1, true));
        var retryScheduled = rig.Clock.WhenScheduled();
        var flush = rig.Batcher.FlushAsync(cancellation.Token);
        await rig.NextAttemptAsync();
        Assert.Equal(Milliseconds(200), await Phase(retryScheduled, "force flush retry remains paced"));
        cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush);
        Assert.Equal(cancellation.Token, failure.CancellationToken);

        var retry = await rig.AdvanceToDispatchAsync(Milliseconds(200));
        Assert.Equal(new KeyNotification("a", 1, true), Assert.Single(retry.Notifications));
        Assert.Equal(2, rig.Attempts.Count);
    }

    private static async Task Phase(Task task, string phase)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10), TestToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Root batcher phase did not complete: {phase}.", exception);
        }
    }

    private static async Task<T> Phase<T>(Task<T> task, string phase)
    {
        await Phase((Task)task, phase);
        return await task;
    }

    private sealed record Attempt(int Number, long Timestamp, KeyNotification[] Notifications);

    private sealed class TestRig : IAsyncDisposable
    {
        private readonly Channel<Attempt> _attempts = Channel.CreateUnbounded<Attempt>();
        private int _attemptNumber;

        public TestRig()
        {
            Batcher = new(Clock, Namespace, new TestOptionsMonitor(Options), Dispatch, Logger);
        }

        public TestClock Clock { get; } = new();
        public TestNamespace Namespace { get; } = new();
        public DisseminationOptions Options { get; } = new() { Enabled = true };
        public TestLogger Logger { get; } = new();
        public DisseminationRootBatcher Batcher { get; }
        public ConcurrentQueue<Attempt> Attempts { get; } = new();
        public Func<Attempt, bool>? OnDispatch { get; set; }

        public async Task<TimeSpan> NotifyAsync(params KeyNotification[] notifications)
        {
            var scheduled = Clock.WhenScheduled();
            Assert.True(Batcher.Notify(notifications));
            return await Phase(scheduled, "notification timer armed");
        }

        public Task<Attempt> NextAttemptAsync() =>
            Phase(_attempts.Reader.ReadAsync(TestToken).AsTask(), "dispatch callback entered");

        public async Task<Attempt> AdvanceToDispatchAsync(TimeSpan elapsed, bool morePending = false)
        {
            var scheduled = morePending ? Clock.WhenScheduled() : null;
            Clock.Advance(elapsed);
            var attempt = await NextAttemptAsync();
            if (scheduled is not null)
            {
                Assert.Equal(Milliseconds(200), await Phase(scheduled, $"next wave armed after attempt {attempt.Number}"));
            }
            else
            {
                await Phase(Batcher.FlushAsync(TestToken), $"attempt {attempt.Number} reconciled");
            }

            return attempt;
        }

        private bool Dispatch(ReadOnlySpan<KeyNotification> notifications)
        {
            var attempt = new Attempt(Interlocked.Increment(ref _attemptNumber), Clock.GetTimestamp(), notifications.ToArray());
            Attempts.Enqueue(attempt);
            Assert.True(_attempts.Writer.TryWrite(attempt));
            return OnDispatch?.Invoke(attempt) ?? true;
        }

        public async ValueTask DisposeAsync()
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await Batcher.StopAsync(cleanup.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (InvalidOperationException) when (Logger.WorkerFailed.Task.IsCompletedSuccessfully)
            {
            }
        }
    }

    private sealed class TestNamespace : IDisseminationNamespace
    {
        public DisseminationNamespace Name => new("root-batcher-test");
        public ConcurrentQueue<DisseminationKey> VersionReads { get; } = new();
        public Func<DisseminationKey, long>? GetVersionHandler { get; set; }
        public DisseminationNamespaceOptions Options { get; } = new()
        {
            Enabled = true,
            MaxCoalescingDelay = TimeSpan.FromMilliseconds(25),
            MaxPendingItemCount = 8192,
        };

        public IEnumerable<DigestEntry> Digests => throw new InvalidOperationException("Root batching must not inspect namespace values.");
        public long GetVersion(DisseminationKey key)
        {
            VersionReads.Enqueue(key);
            return GetVersionHandler?.Invoke(key) ?? 0;
        }

        public DisseminationRepairResult CreateRepair(in DisseminationRepairRequest request) =>
            throw new InvalidOperationException("Only peer queues may materialize payloads.");
        public ValueTask<DisseminationApplyResult> ApplyValueAsync(DisseminationValue value, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Root batching must not apply payloads.");
    }

    private sealed class TestOptionsMonitor(DisseminationOptions options) : IOptionsMonitor<DisseminationOptions>
    {
        public DisseminationOptions CurrentValue => options;
        public DisseminationOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<DisseminationOptions, string?> listener) => null;
    }

    // The test is the sole fake-time driver. A schedule barrier completes only after the actual
    // underlying timer was armed, so advancing time never races timer readiness.
    private sealed class TestClock : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new();
        private TaskCompletionSource<TimeSpan>? _nextSchedule;
        private int _failNextSchedule;
        private int _timerDisposed;
        private int _timerCount;
        private int _scheduleCount;

        public int TimerCount => Volatile.Read(ref _timerCount);
        public int ScheduleCount => Volatile.Read(ref _scheduleCount);
        public bool TimerDisposed => Volatile.Read(ref _timerDisposed) != 0;
        public bool FailNextSchedule { set => Volatile.Write(ref _failNextSchedule, value ? 1 : 0); }
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public void Advance(TimeSpan elapsed) => _inner.Advance(elapsed);

        public Task<TimeSpan> WhenScheduled()
        {
            var completion = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.Null(Interlocked.CompareExchange(ref _nextSchedule, completion, null));
            return completion.Task;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timerCount);
            return new TestTimer(this, _inner.CreateTimer(callback, state, dueTime, period));
        }

        private sealed class TestTimer(TestClock owner, ITimer inner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime > TimeSpan.Zero && Interlocked.Exchange(ref owner._failNextSchedule, 0) != 0)
                {
                    throw new InvalidOperationException("The root timer cannot schedule another wave.");
                }

                var changed = inner.Change(dueTime, period);
                if (dueTime > TimeSpan.Zero)
                {
                    Interlocked.Increment(ref owner._scheduleCount);
                    Interlocked.Exchange(ref owner._nextSchedule, null)?.TrySetResult(dueTime);
                }

                return changed;
            }

            public void Dispose()
            {
                inner.Dispose();
                Volatile.Write(ref owner._timerDisposed, 1);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class TestLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, Exception? Exception)> Entries { get; } = new();
        public TaskCompletionSource<Exception> WorkerFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ThrowOnLog { get; set; }
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue((logLevel, exception));
            if (logLevel == LogLevel.Error && exception is not null)
            {
                WorkerFailed.TrySetResult(exception);
            }

            if (ThrowOnLog)
            {
                throw new InvalidOperationException("The test logger throws.");
            }
        }
    }
}
