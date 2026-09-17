using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans;
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
    private static TimeSpan Period => TimeSpan.FromSeconds(1);
    private static TimeSpan Milliseconds(int value) => TimeSpan.FromMilliseconds(value);

    [Fact]
    public async Task SingleRootContributionSealsWithoutArmingCollectionTimer()
    {
        await using var rig = new TestRig(1);
        Assert.Equal(new(true, Period), await Phase(rig.Publish(0), "single-silo cohort includes the local root"));
        Assert.Equal(rig.Notification(0), Assert.Single(Assert.Single(rig.Attempts).Notifications));
        Assert.Equal(0, rig.Clock.ScheduleCount);
    }

    [Fact]
    public async Task AllActiveIncarnationsIncludingRootSealEarlyAtOriginalNextPeriod()
    {
        await using var rig = new TestRig();
        var first = await rig.StartPublicationAsync(1);
        rig.Clock.Advance(Milliseconds(10));
        var second = rig.Publish(2);
        rig.Clock.Advance(Milliseconds(15));
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Empty(rig.Attempts);

        var local = rig.Publish(0);
        var receipts = await Phase(Task.WhenAll(first, second, local), "all three producer receipts");

        Assert.All(receipts, receipt => Assert.Equal(new(true, Milliseconds(975)), receipt));
        Assert.Equal(
            new KeyNotification[] { rig.Notification(1), rig.Notification(2), rig.Notification(0) },
            Assert.Single(rig.Attempts).Notifications);
        Assert.Equal(1, rig.Clock.ScheduleCount);
        Assert.Empty(rig.Namespace.VersionReads);
    }

    [Fact]
    public async Task MissingProducerTimesOutAtFirstPendingDeadlineDespiteUpdates()
    {
        await using var rig = new TestRig();
        var first = await rig.StartPublicationAsync(0);
        rig.Clock.Advance(Milliseconds(400));
        var newer = rig.Publish(0, 9);
        var peer = rig.Publish(1);
        rig.Clock.Advance(Milliseconds(599));
        Assert.False(first.IsCompleted);
        Assert.False(newer.IsCompleted);
        Assert.False(peer.IsCompleted);
        Assert.Empty(rig.Attempts);

        rig.Clock.Advance(Milliseconds(1));
        var receipts = await Phase(Task.WhenAll(first, newer, peer), "partial cohort at its original deadline");

        Assert.All(receipts, receipt => Assert.Equal(new(true, TimeSpan.Zero), receipt));
        Assert.Equal(new[] { rig.Notification(0, 9), rig.Notification(1) }, Assert.Single(rig.Attempts).Notifications);
        Assert.Equal(1, rig.Clock.ScheduleCount);
    }

    [Fact]
    public async Task ReceiptDelaySubtractsDispatchTimeInsteadOfStartingAnotherPeriod()
    {
        await using var rig = new TestRig(2);
        using var release = new ManualResetEventSlim();
        var token = TestToken;
        rig.OnDispatch = _ =>
        {
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), token), "Receipt dispatch was not released.");
            return true;
        };
        var first = await rig.StartPublicationAsync(0);
        rig.Clock.Advance(Milliseconds(25));
        var second = rig.Publish(1);
        try
        {
            await rig.NextAttemptAsync();
            rig.Clock.Advance(Milliseconds(75));
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        Assert.All(await Phase(Task.WhenAll(first, second), "dispatch time deducted from receipt delay"),
            receipt => Assert.Equal(new(true, Milliseconds(900)), receipt));
    }

    [Fact]
    public async Task InventoryHintsAndDuplicateVersionsNeverFillMissingContributions()
    {
        await using var rig = new TestRig();
        await rig.NotifyAsync(rig.Members.Select(silo => new KeyNotification(silo, 10, true)).ToArray());
        var first = rig.Publish(0, 3);
        var duplicate = rig.Publish(0, 3);
        var older = rig.Publish(0, 1);
        var second = rig.Publish(1);
        rig.Clock.Advance(Milliseconds(999));
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Empty(rig.Attempts);

        rig.Clock.Advance(Milliseconds(1));
        Assert.All(await Phase(Task.WhenAll(first, duplicate, older, second), "hints do not count"),
            receipt => Assert.Equal(new(true, TimeSpan.Zero), receipt));
        Assert.All(Assert.Single(rig.Attempts).Notifications, notification =>
        {
            Assert.Equal(10, notification.Version);
            Assert.True(notification.Force);
        });
        Assert.Empty(rig.Namespace.VersionReads);
    }

    [Fact]
    public async Task HintBeforeFirstProducerAnchorsCohortDeadline()
    {
        await using var rig = new TestRig();
        await rig.NotifyAsync(rig.Notification(1));
        rig.Clock.Advance(Milliseconds(400));
        var first = rig.Publish(0);
        rig.Clock.Advance(Milliseconds(599));
        Assert.False(first.IsCompleted);
        rig.Clock.Advance(Milliseconds(1));
        Assert.Equal(new(true, TimeSpan.Zero), await Phase(first, "deadline remains anchored to first pending hint"));
        Assert.Equal(1, rig.Clock.ScheduleCount);
        Assert.Equal(new[] { rig.Notification(1), rig.Notification(0) }, Assert.Single(rig.Attempts).Notifications);
    }

    [Fact]
    public async Task SealedRetriesReuseOutcomeAndRemainingDelayWithoutAnotherCohort()
    {
        await using var rig = new TestRig(2);
        var first = await rig.StartPublicationAsync(0, 2);
        rig.Clock.Advance(Milliseconds(25));
        var peer = rig.Publish(1);
        Assert.Equal(new(true, Milliseconds(975)), await Phase(first, "first early receipt"));
        await Phase(peer, "second early receipt");
        rig.Clock.Advance(Milliseconds(75));

        Assert.Equal(new(true, Milliseconds(900)), await rig.Publish(0, 2));
        Assert.Equal(new(true, Milliseconds(900)), await rig.Publish(0, 1));
        Assert.Equal(new(true, Milliseconds(900)), await rig.Publish(0, 2, force: true));
        rig.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(new(true, TimeSpan.Zero), await rig.Publish(0, 2));
        Assert.Single(rig.Attempts);
        Assert.Equal(1, rig.Clock.ScheduleCount);
    }

    [Fact]
    public async Task NewCohortRequiresNewVersionsFromEveryProducer()
    {
        await using var rig = new TestRig(2);
        var first = await rig.StartPublicationAsync(0);
        var peer = rig.Publish(1);
        await Phase(Task.WhenAll(first, peer), "first complete cohort");
        rig.Clock.Advance(Period);

        var next = await rig.StartPublicationAsync(0, 2);
        Assert.Equal(new(true, TimeSpan.Zero), await rig.Publish(1));
        rig.Clock.Advance(Milliseconds(25));
        Assert.False(next.IsCompleted);
        Assert.Single(rig.Attempts);
        var fresh = rig.Publish(1, 2);
        Assert.All(await Phase(Task.WhenAll(next, fresh), "new versions fill the next cohort"),
            receipt => Assert.Equal(new(true, Milliseconds(975)), receipt));
        Assert.Equal(2, rig.Attempts.Count);
    }

    [Fact]
    public async Task LateTimerDoesNotCreateEmptyOrCatchupWaves()
    {
        await using var rig = new TestRig();
        var first = await rig.StartPublicationAsync(0);
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(new(true, TimeSpan.Zero), await Phase(first, "late timer receipt"));
        await Phase(rig.Batcher.FlushAsync(TestToken), "late cohort reconciled");
        Assert.Equal(new(true, TimeSpan.Zero), await rig.Publish(0));
        rig.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Single(rig.Attempts);
        Assert.Equal(1, rig.Clock.TimerCount);

        var second = await rig.StartPublicationAsync(0, 2);
        rig.Clock.Advance(Milliseconds(999));
        Assert.False(second.IsCompleted);
        rig.Clock.Advance(Milliseconds(1));
        Assert.Equal(new(true, TimeSpan.Zero), await Phase(second, "new cohort gets its own full period"));
        Assert.Equal(2, rig.Attempts.Count);
    }

    [Fact]
    public async Task CanceledCallerDoesNotEraseContributionOrCancelSharedReceipt()
    {
        await using var rig = new TestRig(2);
        using var cancellation = new CancellationTokenSource();
        var scheduled = rig.Clock.WhenScheduled();
        var canceled = rig.Publish(0, token: cancellation.Token);
        Assert.Equal(Period, await Phase(scheduled, "cancelable publication admitted"));
        var retry = rig.Publish(0);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(retry.IsCompleted);
        rig.Clock.Advance(Milliseconds(25));

        var peer = rig.Publish(1);
        Assert.All(await Phase(Task.WhenAll(retry, peer), "shared receipt survives cancellation"),
            receipt => Assert.Equal(new(true, Milliseconds(975)), receipt));
        Assert.Equal(new[] { rig.Notification(0), rig.Notification(1) }, Assert.Single(rig.Attempts).Notifications);
    }

    [Fact]
    public async Task PreCanceledPublicationDoesNotOpenCohort()
    {
        await using var rig = new TestRig(2);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await rig.Publish(0, token: cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, rig.Clock.ScheduleCount);
        var peer = await rig.StartPublicationAsync(1);
        rig.Clock.Advance(Milliseconds(999));
        Assert.False(peer.IsCompleted);
        rig.Clock.Advance(Milliseconds(1));
        Assert.True((await Phase(peer, "only admitted producer")).Accepted);
        Assert.Equal(rig.Notification(1), Assert.Single(Assert.Single(rig.Attempts).Notifications));
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(2048)]
    public async Task ThousandsOfProducersRetainLatestAtCapacityAndDispatchBoundedChunks(int capacity)
    {
        await using var rig = new TestRig(capacity + 1);
        rig.Namespace.Options.MaxPendingItemCount = capacity;
        rig.Options.MaxBatchItems = 128;
        var receipts = new List<Task<DisseminationPublicationReceipt>> { await rig.StartPublicationAsync(0) };
        for (var i = 1; i < capacity; i++)
        {
            receipts.Add(rig.Publish(i));
        }

        Assert.Equal(new(false, Period), await rig.Publish(capacity));
        receipts.Add(rig.Publish(0, 9));
        receipts.Add(rig.Publish(capacity - 1, 7));
        rig.Clock.Advance(Period);
        Assert.All(await Phase(Task.WhenAll(receipts), "bounded producer cohort"), receipt => Assert.True(receipt.Accepted));

        var attempts = rig.Attempts.ToArray();
        Assert.Equal((capacity + 127) / 128, attempts.Length);
        Assert.All(attempts.Take(attempts.Length - 1), attempt => Assert.Equal(128, attempt.Notifications.Length));
        Assert.Equal((capacity - 1) % 128 + 1, attempts[^1].Notifications.Length);
        var sent = attempts.SelectMany(attempt => attempt.Notifications).ToArray();
        Assert.Equal(rig.Members.Take(capacity).Select(silo => new DisseminationKey(silo)), sent.Select(n => n.Key));
        Assert.Equal(9, sent[0].Version);
        Assert.Equal(7, sent[^1].Version);
        Assert.DoesNotContain(sent, n => n.Key.Equals(new DisseminationKey(rig.Members[capacity])));
        Assert.All(attempts, attempt => Assert.Equal(attempts[0].Timestamp, attempt.Timestamp));
        Assert.Empty(rig.Namespace.VersionReads);

        var later = await rig.StartPublicationAsync(capacity);
        rig.Clock.Advance(Period);
        Assert.True((await Phase(later, "capacity released after admission")).Accepted);
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(2048)]
    public async Task FullCohortPreservesPeerItemAndByteSplitting(int count)
    {
        await using var rig = new TestRig(count);
        rig.Options.MaxBatchItems = 128;
        rig.Options.MaxBatchBytes = 64;
        rig.Namespace.Options.MaxPayloadBytes = sizeof(long);
        rig.Namespace.Options.MaxPendingItemCount = count;
        rig.Namespace.MaterializeValues = true;
        var target = Substitute.For<IDisseminationSystemTarget>();
        var factory = Substitute.For<IInternalGrainFactory>();
        var batches = new ConcurrentQueue<DisseminationBroadcastBatch>();
        target.PushBroadcast(Arg.Any<DisseminationBroadcastBatch>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var batch = call.ArgAt<DisseminationBroadcastBatch>(0);
                batches.Enqueue(batch);
                return Task.FromResult(new DisseminationBroadcastResponse
                {
                    Acknowledgments = batch.Values.ToDictionary(
                        pair => pair.Key, pair => pair.Value.Select(value => new DigestEntry(value.Value.Key, value.Value.ToVersion)).ToList()),
                });
            });
        factory.GetSystemTarget<IDisseminationSystemTarget>(Constants.DisseminationSystemTargetType, rig.Members[1]).Returns(target);
        var queue = new DisseminationBroadcastQueue(
            rig.Clock, rig.Members[0], factory, new TestOptionsMonitor(rig.Options),
            [rig.Namespace], NullLogger<DisseminationBroadcastQueue>.Instance);
        rig.OnDispatch = attempt => queue.NotifyBatch(rig.Members[1], rig.Namespace, attempt.Notifications, immediate: false);
        try
        {
            var receipts = new List<Task<DisseminationPublicationReceipt>> { await rig.StartPublicationAsync(0) };
            for (var i = 1; i < count; i++)
            {
                receipts.Add(rig.Publish(i));
            }

            Assert.All(await Phase(Task.WhenAll(receipts), "all producers sealed without fake time advancement"),
                receipt => Assert.Equal(new(true, Period), receipt));
            await Phase(queue.FlushPendingBroadcast(TestToken), "peer byte-limited chunks acknowledged");

            var values = batches.SelectMany(batch => batch.Values[rig.Namespace.Name]).Select(value => value.Value).ToArray();
            Assert.Equal(count, values.Length);
            Assert.Equal(count, values.Select(value => value.Key).Distinct().Count());
            Assert.Equal(rig.Members.Select(silo => new DisseminationKey(silo)).Order(), values.Select(value => value.Key).Order());
            Assert.All(batches, batch =>
            {
                var payloads = batch.Values[rig.Namespace.Name];
                Assert.InRange(payloads.Count, 1, rig.Options.MaxBatchItems);
                Assert.InRange(payloads.Sum(value => value.Value.Payload.Length), 1, rig.Options.MaxBatchBytes);
                Assert.All(payloads, value => Assert.Equal(1L, BitConverter.ToInt64(value.Value.Payload.Span)));
            });
            Assert.All(rig.Attempts, attempt => Assert.InRange(attempt.Notifications.Length, 1, 128));
        }
        finally
        {
            await queue.StopAsync(TestToken);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task UpdatesDuringDispatchPreserveGenerationForceAndInFlightCapacity(bool firstAccepted, bool newForce)
    {
        await using var rig = new TestRig(2);
        rig.Namespace.Options.MaxPendingItemCount = 1;
        using var release = new ManualResetEventSlim();
        var token = TestToken;
        rig.OnDispatch = attempt =>
        {
            if (attempt.Number == 1)
            {
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), token), "First dispatch was not released.");
            }

            return attempt.Number > 1 || firstAccepted;
        };
        var first = await rig.StartPublicationAsync(0, force: true);
        var scheduled = rig.Clock.WhenScheduled();
        rig.Clock.Advance(Period);
        await rig.NextAttemptAsync();
        Task<DisseminationPublicationReceipt> newer = null!;
        try
        {
            await Phase(Task.Run(async () =>
            {
                Assert.Equal(new(false, Period), await rig.Publish(1));
                newer = rig.Publish(0, 2, newForce);
                Assert.False(newer.IsCompleted);
            }, TestToken), "new generation admitted concurrently with the dispatch callback");
        }
        finally
        {
            release.Set();
        }

        Assert.Equal(firstAccepted, (await Phase(first, "first generation receipt")).Accepted);
        Assert.Equal(Period, await Phase(scheduled, "new generation deadline"));
        rig.Clock.Advance(Period);
        Assert.True((await Phase(newer, "new generation receipt")).Accepted);
        Assert.Equal(new[] { rig.Notification(0, 1, true), rig.Notification(0, 2, newForce) },
            rig.Attempts.SelectMany(attempt => attempt.Notifications));
    }

    [Fact]
    public async Task SameVersionInvalidationDuringDispatchRemainsForced()
    {
        await using var rig = new TestRig();
        using var release = new ManualResetEventSlim();
        var token = TestToken;
        rig.OnDispatch = attempt =>
        {
            if (attempt.Number == 1)
            {
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), token), "Invalidated dispatch was not released.");
            }

            return true;
        };
        await rig.NotifyAsync(new KeyNotification("a", 1, false));
        var scheduled = rig.Clock.WhenScheduled();
        rig.Clock.Advance(Period);
        await rig.NextAttemptAsync();
        try
        {
            await Phase(Task.Run(() =>
            {
                Assert.True(rig.Batcher.Notify([new("a", 1, false)]));
                Assert.True(rig.Batcher.Notify([new("a", 1, true)]));
            }, TestToken), "same-version invalidation admitted during callback");
        }
        finally
        {
            release.Set();
        }

        Assert.Equal(Period, await Phase(scheduled, "invalidation remains pending"));
        rig.Clock.Advance(Period);
        await Phase(rig.Batcher.FlushAsync(TestToken), "same-version invalidation dispatched");
        Assert.Equal(new KeyNotification[] { new("a", 1, false), new("a", 1, true) },
            rig.Attempts.SelectMany(attempt => attempt.Notifications));
    }

    [Fact]
    public async Task OlderInFlightRetryUsesSealedSignalWhileNewerVersionWaitsForNextCohort()
    {
        await using var rig = new TestRig(2);
        using var release = new ManualResetEventSlim();
        var token = TestToken;
        rig.OnDispatch = attempt =>
        {
            if (attempt.Number == 1)
            {
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), token), "First generation was not released.");
            }

            return true;
        };
        var first = await rig.StartPublicationAsync(0);
        var scheduled = rig.Clock.WhenScheduled();
        rig.Clock.Advance(Period);
        await rig.NextAttemptAsync();
        Task<DisseminationPublicationReceipt> newer;
        Task<DisseminationPublicationReceipt> retry;
        try
        {
            newer = rig.Publish(0, 2);
            retry = rig.Publish(0);
            Assert.False(first.IsCompleted);
            Assert.False(newer.IsCompleted);
            Assert.False(retry.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        Assert.All(await Phase(Task.WhenAll(first, retry), "old callers share the sealed generation"),
            receipt => Assert.Equal(new(true, TimeSpan.Zero), receipt));
        Assert.False(newer.IsCompleted);
        Assert.Equal(Period, await Phase(scheduled, "only the new generation waits another period"));
        rig.Clock.Advance(Period);
        Assert.Equal(new(true, TimeSpan.Zero), await Phase(newer, "next generation admitted"));
        Assert.Equal(new[] { rig.Notification(0), rig.Notification(0, 2) },
            rig.Attempts.SelectMany(attempt => attempt.Notifications));
    }

    [Theory]
    [InlineData(false, 1L)]
    [InlineData(true, 1L)]
    [InlineData(true, 2L)]
    public async Task PartialAdmissionRejectsReceiptButRetriesNewestHintAndForce(bool newForce, long version)
    {
        await using var rig = new TestRig(2);
        rig.OnDispatch = attempt => attempt.Number != 1;
        var first = await rig.StartPublicationAsync(0, force: true);
        var scheduled = rig.Clock.WhenScheduled();
        rig.Clock.Advance(Period);
        Assert.Equal(new(false, TimeSpan.Zero), await Phase(first, "partial admission receipt"));
        Assert.Equal(Period, await Phase(scheduled, "bounded hint retry"));
        Assert.Equal(new(false, TimeSpan.Zero), await rig.Publish(0));
        if (newForce)
        {
            Assert.True(rig.Batcher.Notify([rig.Notification(0, version, true)]));
        }

        rig.Clock.Advance(Milliseconds(999));
        Assert.Single(rig.Attempts);
        rig.Clock.Advance(Milliseconds(1));
        await Phase(rig.Batcher.FlushAsync(TestToken), "retained hint admitted on retry");
        Assert.Equal(new[] { rig.Notification(0, 1, true), rig.Notification(0, version, true) },
            rig.Attempts.SelectMany(attempt => attempt.Notifications));
        // A later hint retry is not another publication receipt or another producer contribution.
        Assert.Equal(new(false, TimeSpan.Zero), await rig.Publish(0));
    }

    [Fact]
    public async Task PartialChunkAdmissionResolvesOnlyItsOwnProducerReceipts()
    {
        await using var rig = new TestRig(3);
        rig.Options.MaxBatchItems = 1;
        rig.OnDispatch = attempt => attempt.Number != 2;
        var first = await rig.StartPublicationAsync(0);
        var second = rig.Publish(1);
        var retryScheduled = rig.Clock.WhenScheduled();
        var third = rig.Publish(2);
        var receipts = await Phase(Task.WhenAll(first, second, third), "per-chunk receipts");
        Assert.Equal(new[] { true, false, true }, receipts.Select(receipt => receipt.Accepted));
        Assert.All(receipts, receipt => Assert.Equal(Period, receipt.NextPublicationDelay));
        Assert.Equal(Period, await Phase(retryScheduled, "only rejected chunk retained"));
        rig.Clock.Advance(Period);
        await Phase(rig.Batcher.FlushAsync(TestToken), "one rejected chunk retried");
        Assert.Equal(new[] { rig.Notification(0), rig.Notification(1), rig.Notification(2), rig.Notification(1) },
            rig.Attempts.SelectMany(attempt => attempt.Notifications));
    }

    [Fact]
    public async Task CallbackExceptionRejectsReceiptsRetriesHintsAndReportsFailure()
    {
        await using var rig = new TestRig();
        var failure = new InvalidOperationException("The parent dispatcher failed.");
        rig.OnDispatch = attempt => attempt.Number == 1 ? throw failure : true;
        var publication = await rig.StartPublicationAsync(0, force: true);
        var scheduled = rig.Clock.WhenScheduled();
        rig.Clock.Advance(Period);
        Assert.Equal(new(false, TimeSpan.Zero), await Phase(publication, "exception rejects the producer receipt"));
        Assert.Equal(Period, await Phase(scheduled, "exception retry armed"));
        var entry = Assert.Single(rig.Logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(failure, entry.Exception);
        rig.Clock.Advance(Period);
        await Phase(rig.Batcher.FlushAsync(TestToken), "exception hint recovered");
        Assert.Equal(rig.Notification(0, 1, true), Assert.Single(rig.Attempts.Last().Notifications));
        Assert.Equal(2, rig.Attempts.Count);
    }

    [Fact]
    public async Task CallbackLoggingFailureIsVisibleToReceiptAdmissionAndDrain()
    {
        await using var rig = new TestRig();
        var dispatchFailure = new InvalidOperationException("The parent dispatcher failed.");
        rig.Logger.ThrowOnLog = true;
        rig.OnDispatch = _ => throw dispatchFailure;
        var publication = await rig.StartPublicationAsync(0);
        rig.Clock.Advance(Period);
        var failure = await Phase(rig.Logger.WorkerFailed.Task, "dispatch logging failure recorded");
        Assert.Same(dispatchFailure, Assert.IsType<AggregateException>(failure).InnerExceptions[0]);
        var receipt = await Assert.ThrowsAsync<InvalidOperationException>(() => publication);
        Assert.Same(failure, receipt.InnerException);
        var admission = Assert.Throws<InvalidOperationException>(() => rig.Batcher.Notify([new("b", 1, true)]));
        Assert.Same(failure, admission.InnerException);
        var drain = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Batcher.FlushAsync(TestToken));
        Assert.Same(failure, drain.InnerException);
    }

    [Fact]
    public async Task WorkerFailureIsObservedByActiveAndLaterFlushNotifyPublishAndStop()
    {
        await using var rig = new TestRig();
        using var release = new ManualResetEventSlim();
        var token = TestToken;
        rig.Logger.ThrowOnLog = true;
        rig.OnDispatch = _ =>
        {
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), token), "Failing dispatch was not released.");
            return false;
        };
        await rig.NotifyAsync(new KeyNotification("a", 1, true));
        rig.Clock.Advance(Period);
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
        Assert.Same(failure, (await Assert.ThrowsAsync<InvalidOperationException>(
            () => Phase(activeFlush, "active flush observes terminal failure"))).InnerException);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => rig.Batcher.Notify([new("b", 2, false)])).InnerException);
        Assert.Same(failure, (await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await rig.Publish(0))).InnerException);
        Assert.Same(failure, (await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Batcher.FlushAsync(TestToken))).InnerException);
        Assert.Same(failure, (await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Batcher.StopAsync(TestToken))).InnerException);
        Assert.True(rig.Clock.TimerDisposed);
        Assert.Single(rig.Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisablingRejectsReceiptsAndReenableDoesNotReplay(bool global)
    {
        await using var rig = new TestRig();
        var publication = await rig.StartPublicationAsync(0);
        if (global)
        {
            rig.Options.Enabled = false;
        }
        else
        {
            rig.Namespace.Options.Enabled = false;
        }

        await Phase(rig.Batcher.FlushAsync(TestToken), "disabled work retired");
        Assert.False((await publication).Accepted);
        Assert.False(rig.Batcher.Notify([new("disabled", 2, true)]));
        Assert.False((await rig.Publish(1)).Accepted);
        Assert.Empty(rig.Attempts);
        rig.Options.Enabled = true;
        rig.Namespace.Options.Enabled = true;
        Assert.False((await rig.Publish(0)).Accepted);
        var newer = await rig.StartPublicationAsync(0, 2);
        rig.Clock.Advance(Period);
        Assert.True((await Phase(newer, "new work after reenabling")).Accepted);
        Assert.Equal(rig.Notification(0, 2), Assert.Single(Assert.Single(rig.Attempts).Notifications));
    }

    [Fact]
    public async Task MembershipSnapshotIsStableAndRetiredIncarnationCannotContribute()
    {
        await using var rig = new TestRig();
        var first = await rig.StartPublicationAsync(0);
        var retired = rig.Members[2];
        var replacement = SiloAddress.New(retired.Endpoint, retired.Generation + 1);
        rig.SetMembership([rig.Members[0], rig.Members[1], replacement]);
        Assert.False((await rig.Batcher.PublishAsync(new(retired, 9, true), TestToken)).Accepted);
        Assert.False(rig.Batcher.Notify([new(retired, 9, true)]));
        var other = rig.Publish(1);
        var joined = rig.Batcher.PublishAsync(new(replacement, 1, false), TestToken).AsTask();
        rig.Clock.Advance(Milliseconds(999));
        Assert.False(first.IsCompleted);
        Assert.False(other.IsCompleted);
        Assert.False(joined.IsCompleted);
        Assert.Empty(rig.Attempts);
        rig.Clock.Advance(Milliseconds(1));
        Assert.All(await Phase(Task.WhenAll(first, other, joined), "stable cohort tolerates incarnation replacement"),
            receipt => Assert.Equal(new(true, TimeSpan.Zero), receipt));
        Assert.DoesNotContain(Assert.Single(rig.Attempts).Notifications, n => n.Key.Equals(new DisseminationKey(retired)));
    }

    [Fact]
    public async Task MembershipChangeRetiresPendingReceiptsAndFreesCapacity()
    {
        await using var rig = new TestRig();
        rig.Namespace.Options.MaxPendingItemCount = 1;
        var departed = await rig.StartPublicationAsync(2);
        rig.SetMembership([rig.Members[0], rig.Members[1]]);
        var scheduled = rig.Clock.WhenScheduled();
        var active = rig.Publish(0);
        Assert.False((await Phase(departed, "departed incarnation rejected")).Accepted);
        Assert.Equal(Period, await Phase(scheduled, "replacement pending capacity"));
        rig.Clock.Advance(Period);
        Assert.True((await Phase(active, "active contribution accepted")).Accepted);
        Assert.Equal(rig.Notification(0), Assert.Single(Assert.Single(rig.Attempts).Notifications));
    }

    [Fact]
    public async Task EmptyMembershipReleasesPendingReceiptsWithoutDispatch()
    {
        await using var rig = new TestRig();
        var first = await rig.StartPublicationAsync(0);
        var second = rig.Publish(1);
        rig.SetMembership([]);
        rig.Batcher.WakeForMembershipChange();
        Assert.All(await Phase(Task.WhenAll(first, second), "root and peers no longer eligible"),
            receipt => Assert.False(receipt.Accepted));
        await Phase(rig.Batcher.StopAsync(TestToken), "root loss drains without destinations");
        Assert.Empty(rig.Attempts);
        Assert.True(rig.Clock.TimerDisposed);
    }

    [Fact]
    public async Task RacingMembershipReadCannotReadmitAnAlreadyRetiredIncarnation()
    {
        await using var rig = new TestRig();
        var first = await rig.StartPublicationAsync(0);
        var oldMembership = rig.Membership;
        using var release = new ManualResetEventSlim();
        var token = TestToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        rig.OnMembershipRead = () =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), token), "Stale membership read was not released.");
                return oldMembership;
            }

            return rig.Membership;
        };
        var stale = Task.Run(() => rig.Publish(1), TestToken);
        try
        {
            await Phase(entered.Task, "producer captured old membership");
            rig.SetMembership([rig.Members[0]]);
            await Phase(Task.Run(() => Assert.False(rig.Batcher.Notify([rig.Notification(2)])), TestToken),
                "new membership observed while older callback is blocked");
        }
        finally
        {
            release.Set();
        }

        Assert.False((await Phase(stale, "stale membership cannot reverse retirement")).Accepted);
        rig.Clock.Advance(Period);
        Assert.True((await Phase(first, "remaining root contribution")).Accepted);
        Assert.Equal(rig.Notification(0), Assert.Single(Assert.Single(rig.Attempts).Notifications));
    }

    [Fact]
    public async Task MembershipWakeHandsAllPendingChunksToNewRootWithoutWaiting()
    {
        await using var rig = new TestRig(5);
        rig.Options.MaxBatchItems = 2;
        var first = await rig.StartPublicationAsync(0);
        var second = rig.Publish(1);
        var third = rig.Publish(2);
        Assert.True(rig.Batcher.Notify([rig.Notification(3)]));
        rig.Clock.Advance(Milliseconds(25));
        rig.SetMembership([rig.Members[1], rig.Members[0], rig.Members[2], rig.Members[3]]);
        var routedToNewRoot = new ConcurrentQueue<bool>();
        rig.OnDispatch = _ =>
        {
            routedToNewRoot.Enqueue(!rig.Membership.IsAggregationRoot);
            return true;
        };
        rig.Batcher.WakeForMembershipChange();
        Assert.All(await Phase(Task.WhenAll(first, second, third), "new root handoff receipts"),
            receipt => Assert.Equal(new(true, Milliseconds(975)), receipt));
        Assert.Equal(new[] { 2, 2 }, rig.Attempts.Select(attempt => attempt.Notifications.Length));
        Assert.All(routedToNewRoot, routed => Assert.True(routed));
        Assert.False((await rig.Publish(0, 2)).Accepted);
        await Phase(rig.Batcher.StopAsync(TestToken), "former root stopped");
        Assert.True(rig.Clock.TimerDisposed);
    }

    [Fact]
    public async Task MembershipChangeDuringDispatchSkipsRetiredChunkAndRejectsItsReceipt()
    {
        await using var rig = new TestRig(4);
        rig.Options.MaxBatchItems = 1;
        var first = await rig.StartPublicationAsync(0);
        var second = rig.Publish(1);
        var retired = rig.Publish(2);
        rig.OnDispatch = attempt =>
        {
            if (attempt.Number == 1)
            {
                rig.SetMembership([rig.Members[0], rig.Members[1]]);
            }

            return true;
        };

        await Phase(rig.Batcher.FlushAsync(TestToken), "membership revalidated between chunks");
        var receipts = await Task.WhenAll(first, second, retired);
        Assert.Equal(new[] { true, true, false }, receipts.Select(receipt => receipt.Accepted));
        Assert.Equal(new[] { rig.Notification(0), rig.Notification(1) },
            rig.Attempts.SelectMany(attempt => attempt.Notifications));
    }

    [Fact]
    public async Task RootLossBypassesOldRetryFloorOnceButFailedHandoffRemainsBounded()
    {
        await using var rig = new TestRig(2);
        rig.OnDispatch = _ => false;
        var first = await rig.StartPublicationAsync(0);
        var scheduled = rig.Clock.WhenScheduled();
        rig.Clock.Advance(Period);
        Assert.False((await Phase(first, "first admission rejected")).Accepted);
        Assert.Equal(Period, await Phase(scheduled, "old root retry floor"));
        rig.Clock.Advance(Milliseconds(25));
        rig.SetMembership([rig.Members[1], rig.Members[0]]);
        var handoffScheduled = rig.Clock.WhenScheduled();
        rig.Batcher.WakeForMembershipChange();
        Assert.Equal(Period, await Phase(handoffScheduled, "failed new-root handoff uses one period retry"));
        Assert.Equal(2, rig.Attempts.Count);
        rig.Clock.Advance(Milliseconds(999));
        Assert.Equal(2, rig.Attempts.Count);
        rig.OnDispatch = _ => true;
        rig.Clock.Advance(Milliseconds(1));
        await Phase(rig.Batcher.FlushAsync(TestToken), "handoff retry admitted");
        Assert.Equal(3, rig.Attempts.Count);
        Assert.Equal(Milliseconds(25), rig.Clock.GetElapsedTime(rig.Attempts.First().Timestamp, rig.Attempts.ElementAt(1).Timestamp));
    }

    [Fact]
    public async Task PruneRemovesRetiredKeysAndPreservesNewerOwnerInventory()
    {
        await using var rig = new TestRig();
        rig.Namespace.Options.MaxPendingItemCount = 3;
        await rig.NotifyAsync(new("retired", 1, true), new("retained", 2, false));
        var inventory = new HashSet<DisseminationKey> { "retained" };
        rig.Namespace.GetVersionHandler = key => key.Equals(new DisseminationKey("new")) ? 3 : 0;
        Assert.True(rig.Batcher.Notify([new("new", 3, false)]));
        var scheduled = rig.Clock.WhenScheduled();
        rig.Batcher.Prune(inventory);
        Assert.Equal(Period, await Phase(scheduled, "inventory rechecked outside lock"));
        Assert.True(rig.Batcher.Notify([new("extra", 4, false)]));
        rig.Clock.Advance(Period);
        await Phase(rig.Batcher.FlushAsync(TestToken), "pruned inventory dispatched");
        Assert.Equal(new KeyNotification[] { new("retained", 2, false), new("new", 3, false), new("extra", 4, false) },
            Assert.Single(rig.Attempts).Notifications);
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
        var versionRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Namespace.GetVersionHandler = key =>
        {
            Assert.Equal(new DisseminationKey("a"), key);
            versionRead.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestToken), "Owner version read was not released.");
            return 0;
        };
        var scheduled = rig.Clock.WhenScheduled();
        var prune = Task.Run(() => rig.Batcher.Prune(new HashSet<DisseminationKey>()), TestToken);
        try
        {
            await Phase(versionRead.Task, "pruning captured the old generation");
            await Phase(Task.Run(() => Assert.True(rig.Batcher.Notify([new("a", version, false)])), TestToken),
                "notification admitted while namespace version read is blocked");
        }
        finally
        {
            release.Set();
            await Phase(prune, "pruning reconciled admission generations");
        }

        Assert.Equal(Period, await Phase(scheduled, "racing notification preserved"));
        rig.Clock.Advance(Period);
        await Phase(rig.Batcher.FlushAsync(TestToken), "racing notification dispatched");
        Assert.Equal(new KeyNotification("a", version, false), Assert.Single(Assert.Single(rig.Attempts).Notifications));
    }

    [Fact]
    public async Task PruneDuringDispatchDoesNotEraseReadmittedIdentity()
    {
        await using var rig = new TestRig();
        rig.Namespace.Options.MaxPendingItemCount = 1;
        using var release = new ManualResetEventSlim();
        var token = TestToken;
        rig.OnDispatch = attempt =>
        {
            if (attempt.Number == 1)
            {
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), token), "Pruned dispatch was not released.");
            }

            return true;
        };
        var first = await rig.StartPublicationAsync(0);
        var scheduled = rig.Clock.WhenScheduled();
        rig.Clock.Advance(Period);
        await rig.NextAttemptAsync();
        Task<DisseminationPublicationReceipt> next;
        try
        {
            rig.Batcher.Prune(new HashSet<DisseminationKey>());
            Assert.False((await Phase(first, "pruned in-flight receipt rejected")).Accepted);
            next = rig.Publish(0, 7, force: true);
        }
        finally
        {
            release.Set();
        }

        Assert.Equal(Period, await Phase(scheduled, "readmitted identity gets its own deadline"));
        rig.Clock.Advance(Period);
        Assert.True((await Phase(next, "readmitted identity accepted")).Accepted);
        Assert.Equal(new[] { rig.Notification(0), rig.Notification(0, 7, true) },
            rig.Attempts.SelectMany(attempt => attempt.Notifications));
    }

    [Fact]
    public async Task PublicationRetryProtectsAdmissionFromRacingPruneWithoutContributingTwice()
    {
        await using var rig = new TestRig(2);
        var first = await rig.StartPublicationAsync(0);
        using var release = new ManualResetEventSlim();
        var token = TestToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Namespace.GetVersionHandler = _ =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), token), "Prune version read was not released.");
            return 0;
        };
        var scheduled = rig.Clock.WhenScheduled();
        var prune = Task.Run(() => rig.Batcher.Prune(new HashSet<DisseminationKey>()), TestToken);
        Task<DisseminationPublicationReceipt> retry;
        try
        {
            await Phase(entered.Task, "prune captured pre-retry admission");
            retry = rig.Publish(0);
            Assert.False(first.IsCompleted);
            Assert.False(retry.IsCompleted);
            Assert.Empty(rig.Attempts);
        }
        finally
        {
            release.Set();
            await Phase(prune, "prune reconciled retry");
        }

        Assert.Equal(Period, await Phase(scheduled, "retry retained without changing deadline"));
        var second = rig.Publish(1);
        Assert.All(await Phase(Task.WhenAll(first, retry, second), "original contribution survives prune"),
            receipt => Assert.Equal(new(true, Period), receipt));
        Assert.Equal(new[] { rig.Notification(0), rig.Notification(1) }, Assert.Single(rig.Attempts).Notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamespaceSettingsCallbacksRunOutsideBatcherLock(bool period)
    {
        await using var rig = new TestRig();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Action callback = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestToken), "Namespace settings callback was not released.");
            }
        };
        if (period)
        {
            rig.Namespace.OnPeriodRead = callback;
        }
        else
        {
            rig.Namespace.OnOptionsRead = callback;
        }

        var admission = Task.Run(() => rig.Batcher.Notify([new("a", 1, true)]), TestToken);
        try
        {
            await Phase(entered.Task, "namespace callback entered");
            await Phase(Task.Run(() => rig.Batcher.Prune(new HashSet<DisseminationKey>()), TestToken),
                "another thread enters batcher while namespace callback blocks");
        }
        finally
        {
            release.Set();
        }

        Assert.True(await Phase(admission, "namespace callback returned"));
        await Phase(rig.Batcher.FlushAsync(TestToken), "namespace reentrancy work drained");
        Assert.Equal(new KeyNotification("a", 1, true), Assert.Single(Assert.Single(rig.Attempts).Notifications));
    }

    [Fact]
    public async Task HighPriorityHintsBypassCollectionButFailedRetriesUsePublicationPeriod()
    {
        await using var rig = new TestRig();
        rig.Namespace.Options.Priority = DisseminationPriority.High;
        rig.OnDispatch = attempt => attempt.Number != 1;
        var started = rig.Clock.GetTimestamp();
        var scheduled = rig.Clock.WhenScheduled();
        Assert.True(rig.Batcher.Notify([new("a", 1, true)]));
        Assert.Equal(started, (await rig.NextAttemptAsync()).Timestamp);
        Assert.Equal(Period, await Phase(scheduled, "high priority retry bounded"));
        rig.Clock.Advance(Period);
        await Phase(rig.Batcher.FlushAsync(TestToken), "high priority retry accepted");
        Assert.Equal(2, rig.Attempts.Count);
    }

    [Fact]
    public async Task HighPriorityPublicationsStillRequireEveryProducerOrDeadline()
    {
        await using var rig = new TestRig(2);
        rig.Namespace.Options.Priority = DisseminationPriority.High;
        var first = await rig.StartPublicationAsync(0);
        rig.Clock.Advance(Milliseconds(25));
        Assert.False(first.IsCompleted);
        Assert.Empty(rig.Attempts);
        var second = rig.Publish(1);
        Assert.All(await Phase(Task.WhenAll(first, second), "priority cannot bypass the publication cohort"),
            receipt => Assert.Equal(new(true, Milliseconds(975)), receipt));
        Assert.Equal(2, Assert.Single(rig.Attempts).Notifications.Length);
    }

    [Fact]
    public async Task StopSealsPartialCohortDrainsBoundedChunksAndRejectsFurtherAdmission()
    {
        await using var rig = new TestRig(6);
        rig.Options.MaxBatchItems = 2;
        var receipts = new List<Task<DisseminationPublicationReceipt>> { await rig.StartPublicationAsync(0) };
        for (var i = 1; i < 5; i++)
        {
            receipts.Add(rig.Publish(i));
        }

        await Phase(rig.Batcher.StopAsync(TestToken), "stop seals partial cohort");
        Assert.All(await Task.WhenAll(receipts), receipt => Assert.Equal(new(true, Period), receipt));
        Assert.Equal(new[] { 2, 2, 1 }, rig.Attempts.Select(attempt => attempt.Notifications.Length));
        Assert.False(rig.Batcher.Notify([new("late", 6, true)]));
        Assert.False((await rig.Publish(5)).Accepted);
        Assert.True(rig.Clock.TimerDisposed);
        await rig.Batcher.StopAsync(TestToken);
    }

    [Fact]
    public async Task FlushSealsPartialCohortWithoutStoppingLaterAdmission()
    {
        await using var rig = new TestRig(3);
        var first = await rig.StartPublicationAsync(0);
        rig.Clock.Advance(Milliseconds(25));
        await Phase(rig.Batcher.FlushAsync(TestToken), "flush seals partial cohort");
        Assert.Equal(new(true, Milliseconds(975)), await first);
        Assert.False(rig.Clock.TimerDisposed);
        rig.Clock.Advance(Milliseconds(975));
        var second = await rig.StartPublicationAsync(0, 2);
        rig.Clock.Advance(Period);
        Assert.Equal(new(true, TimeSpan.Zero), await Phase(second, "admission after flush"));
        Assert.Equal(2, rig.Attempts.Count);
    }

    [Fact]
    public async Task StopCancellationEndsRetryBudgetAndPreservesCallerToken()
    {
        await using var rig = new TestRig();
        using var cancellation = new CancellationTokenSource();
        rig.OnDispatch = _ => false;
        var publication = await rig.StartPublicationAsync(0);
        var scheduled = rig.Clock.WhenScheduled();
        var stop = rig.Batcher.StopAsync(cancellation.Token);
        Assert.False((await Phase(publication, "stop's failed attempt rejects receipt")).Accepted);
        Assert.Equal(Period, await Phase(scheduled, "stop retry bounded"));
        Assert.False(stop.IsCompleted);
        Assert.False((await rig.Publish(1)).Accepted);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Phase(stop, "stop budget exhausted"));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        var later = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Batcher.StopAsync(CancellationToken.None));
        Assert.Equal(cancellation.Token, later.CancellationToken);
        Assert.True(rig.Clock.TimerDisposed);
        rig.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Single(rig.Attempts);
    }

    [Fact]
    public async Task StopCancellationDuringDispatchRejectsWaitersAndDoesNotClaimNextChunk()
    {
        await using var rig = new TestRig();
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var token = TestToken;
        rig.Options.MaxBatchItems = 1;
        rig.OnDispatch = _ =>
        {
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), token), "Stop's in-flight callback was not released.");
            return true;
        };
        var first = await rig.StartPublicationAsync(0);
        var second = rig.Publish(1);
        var stop = rig.Batcher.StopAsync(cancellation.Token);
        await rig.NextAttemptAsync();
        try
        {
            cancellation.Cancel();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Phase(stop, "stop callback budget"));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.All(await Phase(Task.WhenAll(first, second), "all in-flight and unclaimed receipts rejected"),
                receipt => Assert.False(receipt.Accepted));
        }
        finally
        {
            release.Set();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Batcher.StopAsync(CancellationToken.None));
        Assert.Single(rig.Attempts);
        Assert.True(rig.Clock.TimerDisposed);
    }

    [Fact]
    public async Task PreCanceledStopRejectsReceiptsWithoutDispatch()
    {
        await using var rig = new TestRig();
        using var cancellation = new CancellationTokenSource();
        var publication = await rig.StartPublicationAsync(0);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Batcher.StopAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False((await Phase(publication, "abandoned producer released")).Accepted);
        Assert.False(rig.Batcher.Notify([new("late", 2, true)]));
        Assert.Empty(rig.Attempts);
        Assert.True(rig.Clock.TimerDisposed);
    }

    [Fact]
    public async Task FlushCancellationLeavesAdmittedWorkForPeriodBoundedRetry()
    {
        await using var rig = new TestRig();
        using var cancellation = new CancellationTokenSource();
        rig.OnDispatch = attempt => attempt.Number != 1;
        var publication = await rig.StartPublicationAsync(0, force: true);
        var scheduled = rig.Clock.WhenScheduled();
        var flush = rig.Batcher.FlushAsync(cancellation.Token);
        Assert.False((await Phase(publication, "flush partial admission receipt")).Accepted);
        Assert.Equal(Period, await Phase(scheduled, "flush retry bounded"));
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        rig.Clock.Advance(Period);
        await Phase(rig.Batcher.FlushAsync(TestToken), "admitted hint survives flush cancellation");
        Assert.Equal(new[] { rig.Notification(0, 1, true), rig.Notification(0, 1, true) },
            rig.Attempts.SelectMany(attempt => attempt.Notifications));
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

        public TestRig(int members = 3)
        {
            Members = Enumerable.Range(0, members).Select(index => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 18000 + index), 1)).ToImmutableArray();
            SetMembership(Members);
            Batcher = new(Clock, Namespace, new TestOptionsMonitor(Options), () => OnMembershipRead?.Invoke() ?? Membership, Dispatch, Logger);
        }

        public TestClock Clock { get; } = new();
        public TestNamespace Namespace { get; } = new();
        public DisseminationOptions Options { get; } = new() { Enabled = true };
        public TestLogger Logger { get; } = new();
        public ImmutableArray<SiloAddress> Members { get; }
        public DisseminationMembershipSnapshot Membership { get; private set; } = null!;
        public DisseminationRootBatcher Batcher { get; }
        public ConcurrentQueue<Attempt> Attempts { get; } = new();
        public Func<Attempt, bool>? OnDispatch { get; set; }
        public Func<DisseminationMembershipSnapshot>? OnMembershipRead { get; set; }

        public void SetMembership(ImmutableArray<SiloAddress> members) =>
            Membership = new(new MembershipVersion((Membership?.MembershipVersion.Value ?? 0) + 1),
                Members[0], members, Options.Overlay);

        public KeyNotification Notification(int producer, long version = 1, bool force = false) =>
            new(Members[producer], version, force);

        public Task<DisseminationPublicationReceipt> Publish(int producer, long version = 1, bool force = false, CancellationToken? token = null) =>
            Batcher.PublishAsync(Notification(producer, version, force), token ?? TestToken).AsTask();

        public async Task<Task<DisseminationPublicationReceipt>> StartPublicationAsync(int producer, long version = 1, bool force = false)
        {
            var scheduled = Clock.WhenScheduled();
            var publication = Publish(producer, version, force);
            Assert.Equal(Period, await Phase(scheduled, "first contribution timer armed"));
            return publication;
        }

        public async Task NotifyAsync(params KeyNotification[] notifications)
        {
            var scheduled = Clock.WhenScheduled();
            Assert.True(Batcher.Notify(notifications));
            Assert.Equal(Period, await Phase(scheduled, "hint collection timer armed"));
        }

        public Task<Attempt> NextAttemptAsync() =>
            Phase(_attempts.Reader.ReadAsync(TestToken).AsTask(), "dispatch callback entered");

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
        private readonly DisseminationNamespaceOptions _options = new()
        {
            Enabled = true,
            MaxCoalescingDelay = TimeSpan.FromMilliseconds(25),
            MaxPendingItemCount = 8192,
        };

        public DisseminationNamespace Name => new("root-batcher-test");
        public ConcurrentQueue<DisseminationKey> VersionReads { get; } = new();
        public Func<DisseminationKey, long>? GetVersionHandler { get; set; }
        public Action? OnOptionsRead { get; set; }
        public Action? OnPeriodRead { get; set; }
        public bool MaterializeValues { get; set; }
        public DisseminationNamespaceOptions Options
        {
            get
            {
                OnOptionsRead?.Invoke();
                return _options;
            }
        }

        public TimeSpan AggregationPeriod
        {
            get
            {
                OnPeriodRead?.Invoke();
                return Period;
            }
        }

        public IEnumerable<DigestEntry> Digests => throw new InvalidOperationException("Root cohorts must not inspect namespace values.");
        public long GetVersion(DisseminationKey key)
        {
            VersionReads.Enqueue(key);
            return GetVersionHandler?.Invoke(key) ?? (MaterializeValues ? 1 : 0);
        }

        public DisseminationRepairResult CreateRepair(in DisseminationRepairRequest request)
        {
            if (!MaterializeValues)
            {
                throw new InvalidOperationException("Only peer queues may materialize payloads.");
            }

            if (request.FromVersion >= 1)
            {
                return DisseminationRepairResult.Current(1);
            }

            if (request.MaxBatchBytes < sizeof(long) || request.MaxPayloadBytes < sizeof(long) || request.MaxItemCount < 1)
            {
                return DisseminationRepairResult.InsufficientCapacity(1);
            }

            return DisseminationRepairResult.Produced(1, [new(request.Key, 0, 1, BitConverter.GetBytes(1L))]);
        }

        public ValueTask<DisseminationApplyResult> ApplyValueAsync(DisseminationValue value, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Root cohorts must not apply payloads.");
    }

    private sealed class TestOptionsMonitor(DisseminationOptions options) : IOptionsMonitor<DisseminationOptions>
    {
        public DisseminationOptions CurrentValue => options;
        public DisseminationOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<DisseminationOptions, string?> listener) => null;
    }

    // Exactly one fake-time driver advances time after a barrier proves the real timer was armed.
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
                    throw new InvalidOperationException("The root timer cannot schedule another cohort.");
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
