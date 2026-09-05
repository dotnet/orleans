using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Statistics;
using TestGrains;
using Xunit;

namespace UnitTests.Runtime
{
    /// <summary>
    /// Tests for activation collector functionality including ticket generation from timestamps.
    /// </summary>
    [TestCategory("BVT"), TestCategory("Runtime")]
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    public class ActivationCollectorTests
    {
        private readonly FakeTimeProvider timeProvider;
        private readonly ActivationCollector collector;

        public ActivationCollectorTests()
        {
            var grainCollectionOptions = Options.Create(new GrainCollectionOptions());
            var logger = NullLogger<ActivationCollector>.Instance;

            this.timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00.000+00:00"));
            this.collector = new ActivationCollector(timeProvider, grainCollectionOptions, logger, new EnvironmentStatisticsProvider(), CreateCatalogInstruments());
        }

        [Theory, TestCategory("Activation")]
        [InlineData("2025-01-01T00:00:00", "2025-01-01T00:00:00")]
        [InlineData("2025-01-01T00:00:01", "2025-01-01T00:01:00")]
        [InlineData("2025-01-01T00:00:59", "2025-01-01T00:01:00")]
        [InlineData("2025-01-01T00:01:01", "2025-01-01T00:02:00")]
        public void MakeTicketFromDateTime(string timestampString, string expectedTicketString)
        {
            var timestamp = DateTime.Parse(timestampString);
            var expectedTicket = DateTime.Parse(expectedTicketString);

            var actualTicket = collector.MakeTicketFromDateTime(timestamp);

            Assert.Equal(expectedTicket, actualTicket);
        }

        [Fact, TestCategory("Activation")]
        public void MakeTicketFromDateTime_MaxValue()
        {
            var expectedTicket = DateTime.MaxValue;

            var actualTicket = collector.MakeTicketFromDateTime(DateTime.MaxValue);

            Assert.Equal(expectedTicket, actualTicket);
        }

        [Fact, TestCategory("Activation")]
        public void MakeTicketFromDateTime_Invalid_BeforeNextTicket()
        {
            var timestamp = this.timeProvider.GetUtcNow().AddMinutes(-5).UtcDateTime;

            Assert.Throws<ArgumentException>(() =>
            {
                var ticket = collector.MakeTicketFromDateTime(timestamp);
            });
        }

        [Fact, TestCategory("Activation")]
        public void TryRescheduleCollection_DoesNotThrow_WhenCollectionTicketIsMaxValue()
        {
            // Simulate an activation whose collector-owned registration sits in the DateTime.MaxValue bucket.
            // That state arises when ScanStale reschedules an activation with
            // KeepAliveUntil = DateTime.MaxValue (from DelayDeactivation(Timeout.InfiniteTimeSpan))
            // and MakeTicketFromDateTime clamps the overflowed timestamp to DateTime.MaxValue
            // (see MakeTicketFromDateTime_MaxValue).
            //
            // Cancelling the keep-alive via DelayDeactivation(TimeSpan.Zero) drives
            // ActivationData into TryRescheduleCollection, which must be able to move the
            // activation out of the MaxValue bucket without throwing.
            var activation = Substitute.For<ICollectibleGrainContext, IActivationWorkingSetMember>();
            ConfigureCollectionRegistrationSlot(activation);
            activation.CollectionAgeLimit.Returns(TimeSpan.FromMinutes(5));
            activation.IsExemptFromCollection.Returns(false);

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var farFuture = DateTime.MaxValue - now;
            collector.ScheduleCollection(activation, farFuture, now);
            Assert.Equal(DateTime.MaxValue, collector.GetCollectionTicketForTesting(activation));

            var rescheduled = false;
            var exception = Record.Exception(() =>
            {
                rescheduled = collector.TryRescheduleCollection(activation);
            });

            Assert.Null(exception);
            Assert.True(rescheduled);
            Assert.Equal(now.AddMinutes(5), collector.GetCollectionTicketForTesting(activation));
        }

        [Fact, TestCategory("Activation")]
        public void CollectionTicket_FollowsBucketAcrossScheduleCancelAndRetire()
        {
            var member = PrepareActivation(5, collector);
            var activation = (ICollectibleGrainContext)member;
            var now = timeProvider.GetUtcNow().UtcDateTime;
            collector.ScheduleCollection(activation, activation.CollectionAgeLimit, now);
            var registration = activation.CollectionRegistration;

            Assert.Equal(now.AddMinutes(5), collector.GetCollectionTicketForTesting(activation));
            Assert.True(collector.TryRescheduleCollection(activation));
            Assert.Equal(now.AddMinutes(5), collector.GetCollectionTicketForTesting(activation));

            timeProvider.Advance(TimeSpan.FromMinutes(1));
            Assert.True(collector.TryRescheduleCollection(activation));
            Assert.Equal(now.AddMinutes(6), collector.GetCollectionTicketForTesting(activation));

            Assert.True(collector.TryCancelCollection(activation));
            Assert.Equal(default, collector.GetCollectionTicketForTesting(activation));
            collector.ScheduleCollection(activation, activation.CollectionAgeLimit, timeProvider.GetUtcNow().UtcDateTime);
            Assert.Equal(now.AddMinutes(6), collector.GetCollectionTicketForTesting(activation));
            Assert.Same(registration, activation.CollectionRegistration);

            ((IActivationWorkingSetObserver)collector).OnDeactivating(member);
            Assert.Equal(default, collector.GetCollectionTicketForTesting(activation));
            Assert.False(collector.TryRescheduleCollection(activation));
        }

        [Fact, TestCategory("Activation")]
        public async Task CollectStaleActivations_ReschedulesClaimsIntoNextBucket()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var ageLimit = TimeSpan.FromMinutes(1);
            var activation = Substitute.For<ICollectibleGrainContext>();
            ConfigureCollectionRegistrationSlot(activation);
            activation.CollectionAgeLimit.Returns(ageLimit);
            activation.TryDeactivateForCollection(
                    Arg.Any<DeactivationReason>(),
                    Arg.Any<DateTime>(),
                    Arg.Any<TimeSpan>(),
                    Arg.Any<bool>(),
                    Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    Assert.Equal(default, collector.GetCollectionTicketForTesting(activation));
                    return ActivationCollectionResult.Reschedule(ageLimit);
                });

            var now = timeProvider.GetUtcNow().UtcDateTime;
            collector.ScheduleCollection(activation, ageLimit, now);
            var registration = activation.CollectionRegistration;
            timeProvider.Advance(ageLimit);
            await collector.CollectStaleActivations(cancellationToken);
            Assert.Equal(now.AddMinutes(2), collector.GetCollectionTicketForTesting(activation));

            timeProvider.Advance(ageLimit);
            await collector.CollectStaleActivations(cancellationToken);
            Assert.Equal(now.AddMinutes(3), collector.GetCollectionTicketForTesting(activation));
            Assert.Same(registration, activation.CollectionRegistration);
            activation.Received(2).TryDeactivateForCollection(
                Arg.Any<DeactivationReason>(), Arg.Any<DateTime>(), ageLimit, true, cancellationToken);
        }

        [Theory, TestCategory("Activation")]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CollectStaleActivations_OldClaimCannotModifyNewClaim(bool rescheduleOldClaim)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var timeout = TimeSpan.FromSeconds(30);
            var ageLimit = TimeSpan.FromMinutes(1);
            var activation = Substitute.For<ICollectibleGrainContext>();
            ConfigureCollectionRegistrationSlot(activation);
            activation.CollectionAgeLimit.Returns(ageLimit);
            var firstClaimEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondClaimEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseFirstClaim = new ManualResetEventSlim();
            using var releaseSecondClaim = new ManualResetEventSlim();
            var claimCount = 0;
            activation.TryDeactivateForCollection(
                    Arg.Any<DeactivationReason>(),
                    Arg.Any<DateTime>(),
                    Arg.Any<TimeSpan>(),
                    Arg.Any<bool>(),
                    Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    var claimNumber = Interlocked.Increment(ref claimCount);
                    if (claimNumber == 1)
                    {
                        firstClaimEntered.SetResult();
                        if (!releaseFirstClaim.Wait(timeout, cancellationToken))
                        {
                            throw new TimeoutException("Timed out releasing the first collection claim.");
                        }

                        return rescheduleOldClaim
                            ? ActivationCollectionResult.Reschedule(ageLimit)
                            : ActivationCollectionResult.Remove;
                    }

                    Assert.Equal(2, claimNumber);
                    secondClaimEntered.SetResult();
                    if (!releaseSecondClaim.Wait(timeout, cancellationToken))
                    {
                        throw new TimeoutException("Timed out releasing the second collection claim.");
                    }

                    return ActivationCollectionResult.Reschedule(ageLimit);
                });

            var now = timeProvider.GetUtcNow().UtcDateTime;
            collector.ScheduleCollection(activation, ageLimit, now);
            var registration = activation.CollectionRegistration;
            timeProvider.Advance(ageLimit);
            var firstScan = Task.Run(() => collector.CollectStaleActivations(cancellationToken), cancellationToken);
            Task secondScan = Task.CompletedTask;
            try
            {
                await firstClaimEntered.Task.WaitAsync(timeout, cancellationToken);
                Assert.True(collector.TryCancelCollection(activation));
                collector.ScheduleCollection(activation, ageLimit, timeProvider.GetUtcNow().UtcDateTime);
                timeProvider.Advance(ageLimit);
                secondScan = Task.Run(() => collector.CollectStaleActivations(cancellationToken), cancellationToken);
                await secondClaimEntered.Task.WaitAsync(timeout, cancellationToken);

                releaseFirstClaim.Set();
                await firstScan.WaitAsync(timeout, cancellationToken);
                Assert.Equal(default, collector.GetCollectionTicketForTesting(activation));

                releaseSecondClaim.Set();
                await secondScan.WaitAsync(timeout, cancellationToken);
                Assert.Equal(now.AddMinutes(3), collector.GetCollectionTicketForTesting(activation));
                Assert.Same(registration, activation.CollectionRegistration);
                Assert.Equal(2, Volatile.Read(ref claimCount));
            }
            finally
            {
                releaseFirstClaim.Set();
                releaseSecondClaim.Set();
                await Task.WhenAll(firstScan, secondScan).WaitAsync(timeout, CancellationToken.None);
            }
        }

        [Fact, TestCategory("Activation")]
        public async Task CollectStaleActivations_ClaimRetriesAfterCursorAdvances()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var timeout = TimeSpan.FromSeconds(30);
            var ageLimit = TimeSpan.FromMinutes(1);
            var activation = Substitute.For<ICollectibleGrainContext>();
            ConfigureCollectionRegistrationSlot(activation);
            activation.CollectionAgeLimit.Returns(ageLimit);
            var claimEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseClaim = new ManualResetEventSlim();
            activation.TryDeactivateForCollection(
                    Arg.Any<DeactivationReason>(),
                    Arg.Any<DateTime>(),
                    Arg.Any<TimeSpan>(),
                    Arg.Any<bool>(),
                    Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    claimEntered.SetResult();
                    if (!releaseClaim.Wait(timeout, cancellationToken))
                    {
                        throw new TimeoutException("Timed out releasing the collection claim after cursor advancement.");
                    }

                    return ActivationCollectionResult.Reschedule(ageLimit);
                });

            var now = timeProvider.GetUtcNow().UtcDateTime;
            collector.ScheduleCollection(activation, ageLimit, now);
            timeProvider.Advance(ageLimit);
            var scan = Task.Run(() => collector.CollectStaleActivations(cancellationToken), cancellationToken);
            try
            {
                await claimEntered.Task.WaitAsync(timeout, cancellationToken);
                timeProvider.Advance(TimeSpan.FromMinutes(2));
                await collector.CollectStaleActivations(cancellationToken).WaitAsync(timeout, cancellationToken);
                releaseClaim.Set();
                await scan.WaitAsync(timeout, cancellationToken);
                Assert.Equal(now.AddMinutes(5), collector.GetCollectionTicketForTesting(activation));
                Assert.True(collector.TryCancelCollection(activation));
                Assert.Equal(default, collector.GetCollectionTicketForTesting(activation));
            }
            finally
            {
                releaseClaim.Set();
                await scan.WaitAsync(timeout, CancellationToken.None);
            }
        }

        [Theory, TestCategory("Activation")]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CollectionScans_VisitEveryRegistrationDuringRemoval(bool scanStale)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var observer = (IActivationWorkingSetObserver)collector;
            var activations = new ICollectibleGrainContext[8];
            for (var i = 0; i < activations.Length; i++)
            {
                var member = PrepareActivation(1, collector);
                var activation = (ICollectibleGrainContext)member;
                activations[i] = activation;
                activation.TryDeactivateForCollection(
                        Arg.Any<DeactivationReason>(),
                        Arg.Any<DateTime>(),
                        Arg.Any<TimeSpan>(),
                        Arg.Any<bool>(),
                        Arg.Any<CancellationToken>())
                    .Returns(_ =>
                    {
                        observer.OnDeactivating(member);
                        return ActivationCollectionResult.StartedDeactivation;
                    });
                observer.OnAdded(member);
            }

            timeProvider.Advance(TimeSpan.FromMinutes(1));
            await (scanStale
                ? collector.CollectStaleActivations(cancellationToken)
                : collector.CollectActivations(TimeSpan.FromMinutes(1), cancellationToken));

            foreach (var activation in activations)
            {
                activation.Received(1).TryDeactivateForCollection(
                    Arg.Any<DeactivationReason>(), Arg.Any<DateTime>(), TimeSpan.FromMinutes(1), true, cancellationToken);
                Assert.False(collector.HasActiveCollectionRegistrationForTesting(activation));
                Assert.Equal(default, collector.GetCollectionTicketForTesting(activation));
            }

            Assert.Equal(0, collector._activationCount);
        }

        [Fact, TestCategory("Activation")]
        public void ScheduleCollection_DoesNotAcquireContextMonitor()
        {
            var activation = Substitute.For<ICollectibleGrainContext>();
            ConfigureCollectionRegistrationSlot(activation);
            activation.IsExemptFromCollection.Returns(_ =>
            {
                Assert.False(Monitor.IsEntered(activation));
                return false;
            });

            var now = timeProvider.GetUtcNow().UtcDateTime;
            collector.ScheduleCollection(activation, TimeSpan.FromMinutes(1), now);

            Assert.NotEqual(default, collector.GetCollectionTicketForTesting(activation));
        }

        [Fact, TestCategory("Activation")]
        public async Task TryRescheduleCollection_DoesNotSerializeIndependentContexts()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var ageLimit = TimeSpan.FromMinutes(5);
            var first = Substitute.For<ICollectibleGrainContext>();
            var second = Substitute.For<ICollectibleGrainContext>();
            ConfigureCollectionRegistrationSlot(first);
            ConfigureCollectionRegistrationSlot(second);
            first.IsExemptFromCollection.Returns(false);
            second.IsExemptFromCollection.Returns(false);
            second.CollectionAgeLimit.Returns(ageLimit);

            var now = timeProvider.GetUtcNow().UtcDateTime;
            collector.ScheduleCollection(first, ageLimit, now);
            collector.ScheduleCollection(second, ageLimit, now);

            var metadataRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseMetadata = new ManualResetEventSlim();
            first.CollectionAgeLimit.Returns(_ =>
            {
                metadataRequested.TrySetResult();
                if (!releaseMetadata.Wait(TimeSpan.FromSeconds(10), cancellationToken))
                {
                    throw new TimeoutException("Timed out waiting to release collection metadata access.");
                }

                return ageLimit;
            });

            var firstReschedule = Task.Run(() => collector.TryRescheduleCollection(first), cancellationToken);
            await metadataRequested.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            try
            {
                Assert.True(
                    await Task.Run(
                        () => collector.TryRescheduleCollection(second),
                        cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));
            }
            finally
            {
                releaseMetadata.Set();
            }

            Assert.True(await firstReschedule.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));
        }

        [Fact, TestCategory("Activation")]
        public async Task CollectStaleActivations_DelegatesAtomicTransitionToContext()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var ageLimit = TimeSpan.FromMinutes(1);
            var activation = Substitute.For<ICollectibleGrainContext>();
            ConfigureCollectionRegistrationSlot(activation);
            activation.CollectionAgeLimit.Returns(ageLimit);
            activation.IsExemptFromCollection.Returns(false);
            activation.TryDeactivateForCollection(
                    Arg.Any<DeactivationReason>(),
                    Arg.Any<DateTime>(),
                    Arg.Any<TimeSpan>(),
                    Arg.Any<bool>(),
                    Arg.Any<CancellationToken>())
                .Returns(ActivationCollectionResult.StartedDeactivation);
            activation.Deactivated.Returns(Task.CompletedTask);

            var scheduledAt = timeProvider.GetUtcNow().UtcDateTime;
            collector.ScheduleCollection(activation, ageLimit, scheduledAt);
            timeProvider.Advance(ageLimit);

            await collector.CollectStaleActivations(cancellationToken);

            activation.Received(1).TryDeactivateForCollection(
                Arg.Is<DeactivationReason>(reason => reason.ReasonCode == DeactivationReasonCode.ActivationIdle),
                timeProvider.GetUtcNow().UtcDateTime,
                ageLimit,
                true,
                cancellationToken);
            Assert.Equal(default, collector.GetCollectionTicketForTesting(activation));
        }

        [Fact, TestCategory("Activation")]
        public async Task CollectStaleActivations_CancellationInvalidatesClaim()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var ageLimit = TimeSpan.FromMinutes(1);
            var activation = Substitute.For<ICollectibleGrainContext>();
            ConfigureCollectionRegistrationSlot(activation);
            activation.CollectionAgeLimit.Returns(ageLimit);
            activation.IsExemptFromCollection.Returns(false);
            activation.TryDeactivateForCollection(
                    Arg.Any<DeactivationReason>(),
                    Arg.Any<DateTime>(),
                    Arg.Any<TimeSpan>(),
                    Arg.Any<bool>(),
                    Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    Assert.True(collector.TryCancelCollection(activation));
                    return ActivationCollectionResult.Reschedule(ageLimit);
                });

            var scheduledAt = timeProvider.GetUtcNow().UtcDateTime;
            collector.ScheduleCollection(activation, ageLimit, scheduledAt);
            timeProvider.Advance(ageLimit);

            await collector.CollectStaleActivations(cancellationToken);

            Assert.Equal(default, collector.GetCollectionTicketForTesting(activation));
        }

        [Fact, TestCategory("Activation")]
        public void OnDeactivating_RemovesCollectionRegistration()
        {
            var activation = PrepareActivation(1, collector);
            var observer = (IActivationWorkingSetObserver)collector;

            observer.OnAdded(activation);
            Assert.True(collector.HasActiveCollectionRegistrationForTesting((ICollectibleGrainContext)activation));

            observer.OnDeactivating(activation);

            Assert.False(collector.HasActiveCollectionRegistrationForTesting((ICollectibleGrainContext)activation));
            Assert.False(collector.TryRescheduleCollection((ICollectibleGrainContext)activation));
        }

        [Fact, TestCategory("Activation")]
        public async Task OnDeactivating_PreventsInFlightReschedule()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var ageLimit = TimeSpan.FromMinutes(1);
            var activation = PrepareActivation(ageLimit, collector);
            var collectible = (ICollectibleGrainContext)activation;
            var observer = (IActivationWorkingSetObserver)collector;
            observer.OnAdded(activation);

            var metadataRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseMetadata = new ManualResetEventSlim();
            collectible.CollectionAgeLimit.Returns(_ =>
            {
                metadataRequested.TrySetResult();
                if (!releaseMetadata.Wait(TimeSpan.FromSeconds(10), cancellationToken))
                {
                    throw new TimeoutException("Timed out waiting to release collection metadata access.");
                }

                return ageLimit;
            });

            var reschedule = Task.Run(() => collector.TryRescheduleCollection(collectible), cancellationToken);
            await metadataRequested.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            try
            {
                observer.OnDeactivating(activation);
            }
            finally
            {
                releaseMetadata.Set();
            }

            Assert.False(await reschedule.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));
            Assert.False(collector.HasActiveCollectionRegistrationForTesting(collectible));
            Assert.Equal(default, collector.GetCollectionTicketForTesting(collectible));
        }

        [Fact, TestCategory("Activation")]
        public void OnEvicted_DoesNotRecreateRetiredCollectionRegistration()
        {
            var activation = PrepareActivation(1, collector);
            var collectible = (ICollectibleGrainContext)activation;
            var observer = (IActivationWorkingSetObserver)collector;
            observer.OnAdded(activation);

            observer.OnDeactivating(activation);
            observer.OnDeactivated(activation);
            observer.OnEvicted(activation);

            Assert.False(collector.HasActiveCollectionRegistrationForTesting(collectible));
            Assert.Equal(default, collector.GetCollectionTicketForTesting(collectible));
        }

        [Theory, TestCategory("MemoryBasedDeactivations")]
        [InlineData(80.0, 70.0, 1000, 150, 100, true, 82)] // Over threshold, need to deactivate
        [InlineData(80.0, 70.0, 1000, 250, 100, false, 0)] // Below threshold, no deactivation
        [InlineData(80.0, 70.0, 1000, 100, 200, true, 155)] // More activations, smaller per-activation size
        [InlineData(80.0, 70.0, 1000, 800, 100, false, 0)] // Well below threshold
        [InlineData(80.0, 70.0, 1000, 50, 10, true, 7)] // Few activations, large per-activation size
        [InlineData(80.0, 70.0, 1000, 100, 0, false, 0)] // No activations
        public void IsMemoryOverloaded_WorksAsExpected(
            double memoryLoadThreshold,
            double targetMemoryLoad,
            long maxMemoryMb,
            long availableMemoryMb,
            int activationCount,
            bool expectedOverloaded,
            int expectedActivationsTarget)
        {
            var grainCollectionOptions = Options.Create(new GrainCollectionOptions
            {
                MemoryUsageLimitPercentage = memoryLoadThreshold,
                MemoryUsageTargetPercentage = targetMemoryLoad
            });

            // Calculate usedMemory and set rawAvailableMemoryBytes as per new logic
            long usedMemoryBytes = maxMemoryMb - availableMemoryMb;
            long rawAvailableMemoryBytes = availableMemoryMb;
            long maxMemoryBytes = maxMemoryMb;

            var statsProvider = Substitute.For<IEnvironmentStatisticsProvider>();
            statsProvider.GetEnvironmentStatistics().Returns(
                new EnvironmentStatistics(
                    cpuUsagePercentage: 0,
                    rawCpuUsagePercentage: 0,
                    memoryUsageBytes: usedMemoryBytes,
                    rawMemoryUsageBytes: usedMemoryBytes,
                    availableMemoryBytes: rawAvailableMemoryBytes,
                    rawAvailableMemoryBytes: rawAvailableMemoryBytes,
                    maximumAvailableMemoryBytes: maxMemoryBytes
                )
            );

            var logger = NullLogger<ActivationCollector>.Instance;
            var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);

            var collector = new ActivationCollector(
                timeProvider,
                grainCollectionOptions,
                logger,
                statsProvider,
                CreateCatalogInstruments()
            );

            collector._activationCount = activationCount;
            var overloaded = collector.IsMemoryOverloaded(out var surplusActivations);

            Assert.Equal(expectedOverloaded, overloaded);
            if (overloaded)
            {
                Assert.Equal(expectedActivationsTarget, activationCount - surplusActivations);
            }
            else
            {
                Assert.Equal(0, surplusActivations);
            }
        }

        public bool WasRemovedByCollection { get; set; }

        [Fact]
        public void IsMemoryOverloaded_DoesNotQueryStats_WhenNoActivations()
        {
            var grainCollectionOptions = Options.Create(new GrainCollectionOptions());
            var statsProvider = Substitute.For<IEnvironmentStatisticsProvider>();
            var logger = NullLogger<ActivationCollector>.Instance;
            var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var collector = new ActivationCollector(timeProvider, grainCollectionOptions, logger, statsProvider, CreateCatalogInstruments());

            collector._activationCount = 0;
            var overloaded = collector.IsMemoryOverloaded(out var surplusActivations);

            Assert.False(overloaded);
            Assert.Equal(0, surplusActivations);
            statsProvider.DidNotReceive().GetEnvironmentStatistics();
        }

        [Fact]
        public async Task DeactivateInDueTimeOrder_OnlyOldestAndEligibleAreDeactivated()
        {
            var grainCollectionOptions = Options.Create(new GrainCollectionOptions());

            var logger = NullLogger<ActivationCollector>.Instance;
            var statsProvider = Substitute.For<IEnvironmentStatisticsProvider>();
            var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);

            var collector = new ActivationCollector(timeProvider, grainCollectionOptions, logger, statsProvider, CreateCatalogInstruments());
            var timer = Substitute.For<IAsyncTimer>();
            timer.NextTick().Returns(Task.FromResult(false));
            var timerFactory = Substitute.For<IAsyncTimerFactory>();
            timerFactory.Create(Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<TimeProvider>()).Returns(timer);

            var wsLogger = NullLogger<ActivationWorkingSet>.Instance;
            var workingSet = new ActivationWorkingSet(timerFactory, wsLogger, new[] { collector }, CreateCatalogInstruments(), TimeProvider.System);

            var activation1 = PrepareActivation(1, collector);
            var activation2 = PrepareActivation(1, collector);
            var activation3 = PrepareActivation(1, collector);

            activation1.IsCandidateForRemoval(Arg.Any<bool>()).Returns(true);
            activation2.IsCandidateForRemoval(Arg.Any<bool>()).Returns(true);
            activation3.IsCandidateForRemoval(Arg.Any<bool>()).Returns(true);

            workingSet.OnActivated(activation1);
            workingSet.OnActivated(activation2);
            workingSet.OnActivated(activation3);

            await collector.DeactivateInDueTimeOrder(2, CancellationToken.None);

            Assert.Equal(1, collector._activationCount);
        }

        [Fact]
        public async Task DeactivateInDueTimeOrder_ConcurrentModification_ShouldNotThrow()
        {
            var grainCollectionOptions = Options.Create(new GrainCollectionOptions());

            var logger = NullLogger<ActivationCollector>.Instance;
            var statsProvider = Substitute.For<IEnvironmentStatisticsProvider>();
            var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);

            var collector = new ActivationCollector(timeProvider, grainCollectionOptions, logger, statsProvider, CreateCatalogInstruments());
            var timer = Substitute.For<IAsyncTimer>();
            timer.NextTick().Returns(Task.FromResult(false));
            var timerFactory = Substitute.For<IAsyncTimerFactory>();
            timerFactory.Create(Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<TimeProvider>()).Returns(timer);

            var wsLogger = NullLogger<ActivationWorkingSet>.Instance;
            var workingSet = new ActivationWorkingSet(timerFactory, wsLogger, new[] { collector }, CreateCatalogInstruments(), TimeProvider.System);

            var totalActivations = 500;
            var activations = new List<IActivationWorkingSetMember>();

            for (var i = 0; i < totalActivations; i++)
            {
                var collectionAgeLimit = TimeSpan.FromMinutes(1) + TimeSpan.FromMinutes(i * 1);

                var activation = PrepareActivation(collectionAgeLimit, collector);

                activation.IsCandidateForRemoval(Arg.Any<bool>()).Returns(true);
                var activationMember = activation;
                activations.Add(activationMember);
                workingSet.OnActivated(activationMember);
            }

            // Now we have 500 buckets. Let's trigger the race condition.
            var exceptions = new ConcurrentBag<Exception>();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

            // Task 1: Aggressively ADD new activations (creates NEW buckets in the dictionary)
            var addTask = Task.Run(async () =>
            {
                int addCount = 0;
                while (!cts.Token.IsCancellationRequested && addCount < 200)
                {
                    // Add 10 activations at a time with random collection ages
                    for (int i = 0; i < 10; i++)
                    {
                        var activation = PrepareActivation(501 + Random.Shared.Next(200), collector);
                        activation.IsCandidateForRemoval(Arg.Any<bool>()).Returns(true);

                        workingSet.OnActivated(activation);
                        addCount++;
                    }

                    await Task.Yield();
                }
            }, cts.Token);

            // Task 2: Aggressively REMOVE activations (empties buckets, causing REMOVAL from dictionary)
            var removeTask = Task.Run(async () =>
            {
                int removeCount = 0;
                while (!cts.Token.IsCancellationRequested && removeCount < 200)
                {
                    // Remove 10 activations at a time
                    for (int i = 0; i < 10 && activations.Count > 100; i++)
                    {
                        var activation = activations[Random.Shared.Next(activations.Count)] as ICollectibleGrainContext;

                        // TryCancelCollection removes the activation from its bucket
                        // If the bucket becomes empty, it gets removed from the dictionary!
                        if (collector.TryCancelCollection(activation))
                        {
                            removeCount++;
                        }
                    }

                    await Task.Yield();
                }
            }, cts.Token);

            // Task 3: Run DeactivateInDueTimeOrder MANY times concurrently
            // This is where the collector snapshots and sorts buckets while they are being added and removed.
            var deactivateTasks = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
            {
                for (int i = 0; i < 100; i++)
                {
                    try
                    {
                        // Deactivation iterates through the buckets, and if code is not resilient for concurrent modification,
                        // it will blow up with some form of collection modification exception.                        
                        await collector.DeactivateInDueTimeOrder(50, cts.Token);
                        await Task.Delay(1, cts.Token);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            }, cts.Token)).ToArray();

            // Wait for all deactivation attempts
            await Task.WhenAll(deactivateTasks);

            // Stop background modifications
            cts.Cancel();
            await Task.WhenAll(addTask, removeTask);

            // Verify no exceptions occurred during deactivation
            Assert.Empty(exceptions);
        }

        [Fact]
        public async Task DeactivateInDueTimeOrder_SkipsActiveAndInvalidActivations()
        {
            var grainCollectionOptions = Options.Create(new GrainCollectionOptions());

            var logger = NullLogger<ActivationCollector>.Instance;
            var statsProvider = Substitute.For<IEnvironmentStatisticsProvider>();
            var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);

            var collector = new ActivationCollector(timeProvider, grainCollectionOptions, logger, statsProvider, CreateCatalogInstruments());
            var timer = Substitute.For<IAsyncTimer>();
            timer.NextTick().Returns(Task.FromResult(false));
            var timerFactory = Substitute.For<IAsyncTimerFactory>();
            timerFactory.Create(Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<TimeProvider>()).Returns(timer);

            var wsLogger = NullLogger<ActivationWorkingSet>.Instance;
            var workingSet = new ActivationWorkingSet(timerFactory, wsLogger, new[] { collector }, CreateCatalogInstruments(), TimeProvider.System);

            var inactiveActivation1 = PrepareActivation(1, collector);
            var activeActivation = PrepareActivation(1, collector);
            var invalidActivation = PrepareActivation(1, collector);
            var inactiveActivation2 = PrepareActivation(1, collector);

            inactiveActivation1.IsCandidateForRemoval(Arg.Any<bool>()).Returns(true);
            activeActivation.IsCandidateForRemoval(Arg.Any<bool>()).Returns(true);
            invalidActivation.IsCandidateForRemoval(Arg.Any<bool>()).Returns(true);
            inactiveActivation2.IsCandidateForRemoval(Arg.Any<bool>()).Returns(true);

            workingSet.OnActivated(inactiveActivation1);
            workingSet.OnActivated(activeActivation);
            workingSet.OnActivated(invalidActivation);
            workingSet.OnActivated(inactiveActivation2);

            ((ICollectibleGrainContext)activeActivation)
                .TryDeactivateForCollection(
                    Arg.Any<DeactivationReason>(),
                    Arg.Any<DateTime>(),
                    Arg.Any<TimeSpan>(),
                    Arg.Any<bool>(),
                    Arg.Any<CancellationToken>())
                .Returns(ActivationCollectionResult.Reschedule(TimeSpan.FromMinutes(1)));
            ((ICollectibleGrainContext)invalidActivation)
                .TryDeactivateForCollection(
                    Arg.Any<DeactivationReason>(),
                    Arg.Any<DateTime>(),
                    Arg.Any<TimeSpan>(),
                    Arg.Any<bool>(),
                    Arg.Any<CancellationToken>())
                .Returns(ActivationCollectionResult.Remove);

            await collector.DeactivateInDueTimeOrder(4, CancellationToken.None);

            ((ICollectibleGrainContext)inactiveActivation1).Received(1).TryDeactivateForCollection(
                Arg.Any<DeactivationReason>(),
                Arg.Any<DateTime>(),
                TimeSpan.Zero,
                false,
                Arg.Any<CancellationToken>());
            ((ICollectibleGrainContext)inactiveActivation2).Received(1).TryDeactivateForCollection(
                Arg.Any<DeactivationReason>(),
                Arg.Any<DateTime>(),
                TimeSpan.Zero,
                false,
                Arg.Any<CancellationToken>());
            ((ICollectibleGrainContext)activeActivation).Received(1).TryDeactivateForCollection(
                Arg.Any<DeactivationReason>(),
                Arg.Any<DateTime>(),
                TimeSpan.Zero,
                false,
                Arg.Any<CancellationToken>());
            ((ICollectibleGrainContext)invalidActivation).Received(1).TryDeactivateForCollection(
                Arg.Any<DeactivationReason>(),
                Arg.Any<DateTime>(),
                TimeSpan.Zero,
                false,
                Arg.Any<CancellationToken>());
            Assert.Equal(2, collector._activationCount);
        }

        [Fact, TestCategory("Activation")]
        public async Task WorkingSetScan_DoesNotUpdateReaddedMember()
        {
            var timer = Substitute.For<IAsyncTimer>();
            timer.NextTick().Returns(Task.FromResult(true), Task.FromResult(false));
            var timerFactory = Substitute.For<IAsyncTimerFactory>();
            timerFactory.Create(Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<TimeProvider>()).Returns(timer);
            var observer = Substitute.For<IActivationWorkingSetObserver>();
            var workingSet = new ActivationWorkingSet(
                timerFactory,
                NullLogger<ActivationWorkingSet>.Instance,
                [observer],
                CreateCatalogInstruments(),
                TimeProvider.System);
            var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var resumeScan = new ManualResetEventSlim();
            var member = new TestWorkingSetMember(wouldRemove =>
            {
                Assert.False(wouldRemove);
                scanStarted.TrySetResult();
                resumeScan.Wait();
                return true;
            });
            workingSet.OnActivated(member);

            var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
            ((ILifecycleParticipant<ISiloLifecycle>)workingSet).Participate(lifecycle);
            var mutationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? mutationTask = null;
            Task? stopTask = null;
            var lifecycleStarted = false;
            try
            {
                await lifecycle.OnStart(TestContext.Current.CancellationToken);
                lifecycleStarted = true;
                await scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                mutationTask = Task.Run(() =>
                {
                    mutationStarted.SetResult();
                    workingSet.OnEvicted(member);
                    workingSet.OnActivated(member);
                }, TestContext.Current.CancellationToken);
                await mutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                stopTask = lifecycle.OnStop(TestContext.Current.CancellationToken);
                Assert.False(stopTask.IsCompleted);
                Assert.False(mutationTask.IsCompleted);
                resumeScan.Set();
                await mutationTask;
                await stopTask;
            }
            finally
            {
                resumeScan.Set();
                if (mutationTask is not null)
                {
                    await ObserveCleanup(mutationTask);
                }

                if (lifecycleStarted)
                {
                    stopTask ??= lifecycle.OnStop(CancellationToken.None);
                    await ObserveCleanup(stopTask);
                }
            }

            Assert.Equal(1, workingSet.Count);
            Assert.Contains(member, workingSet.Members);
            observer.Received(2).OnAdded(member);
            observer.Received(1).OnEvicted(member);
            observer.Received(1).OnIdle(member);
        }

        [Fact, TestCategory("Activation")]
        public async Task WorkingSetScan_DoesNotRemoveReaddedMember()
        {
            var timer = Substitute.For<IAsyncTimer>();
            timer.NextTick().Returns(Task.FromResult(true), Task.FromResult(true), Task.FromResult(false));
            var timerFactory = Substitute.For<IAsyncTimerFactory>();
            timerFactory.Create(Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<TimeProvider>()).Returns(timer);
            var observer = Substitute.For<IActivationWorkingSetObserver>();
            var workingSet = new ActivationWorkingSet(
                timerFactory,
                NullLogger<ActivationWorkingSet>.Instance,
                [observer],
                CreateCatalogInstruments(),
                TimeProvider.System);
            var removalScanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var resumeScan = new ManualResetEventSlim();
            var member = new TestWorkingSetMember(wouldRemove =>
            {
                if (!wouldRemove)
                {
                    return true;
                }

                removalScanStarted.TrySetResult();
                resumeScan.Wait();
                return true;
            });
            workingSet.OnActivated(member);

            var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
            ((ILifecycleParticipant<ISiloLifecycle>)workingSet).Participate(lifecycle);
            var mutationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? mutationTask = null;
            Task? stopTask = null;
            var lifecycleStarted = false;
            try
            {
                await lifecycle.OnStart(TestContext.Current.CancellationToken);
                lifecycleStarted = true;
                await removalScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                mutationTask = Task.Run(() =>
                {
                    mutationStarted.SetResult();
                    workingSet.OnEvicted(member);
                    workingSet.OnActivated(member);
                }, TestContext.Current.CancellationToken);
                await mutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                stopTask = lifecycle.OnStop(TestContext.Current.CancellationToken);
                Assert.False(stopTask.IsCompleted);
                Assert.False(mutationTask.IsCompleted);
                resumeScan.Set();
                await mutationTask;
                await stopTask;
            }
            finally
            {
                resumeScan.Set();
                if (mutationTask is not null)
                {
                    await ObserveCleanup(mutationTask);
                }

                if (lifecycleStarted)
                {
                    stopTask ??= lifecycle.OnStop(CancellationToken.None);
                    await ObserveCleanup(stopTask);
                }
            }

            Assert.Equal(1, workingSet.Count);
            Assert.Contains(member, workingSet.Members);
            observer.Received(2).OnAdded(member);
            observer.Received(1).OnIdle(member);
            observer.Received(1).OnEvicted(member);
        }

        [Fact, TestCategory("Activation")]
        public async Task WorkingSetScan_RepeatedCyclesPreserveCountAndObserverConsistency()
        {
            const int reactivationCycles = 64;
            const int removalVisits = 2;
            const int totalVisits = reactivationCycles * 2 + removalVisits;
            var ticks = 0;
            var timer = Substitute.For<IAsyncTimer>();
            timer.NextTick().Returns(_ => Task.FromResult(Interlocked.Increment(ref ticks) <= totalVisits));
            var timerFactory = Substitute.For<IAsyncTimerFactory>();
            timerFactory.Create(Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<TimeProvider>()).Returns(timer);
            var observer = Substitute.For<IActivationWorkingSetObserver>();
            var workingSet = new ActivationWorkingSet(
                timerFactory,
                NullLogger<ActivationWorkingSet>.Instance,
                [observer],
                CreateCatalogInstruments(),
                TimeProvider.System);
            var visits = 0;
            var member = new TestWorkingSetMember(_ =>
            {
                var visit = visits++;
                return visit >= reactivationCycles * 2 || (visit & 1) == 0;
            });
            workingSet.OnActivated(member);

            var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
            ((ILifecycleParticipant<ISiloLifecycle>)workingSet).Participate(lifecycle);
            await lifecycle.OnStart(TestContext.Current.CancellationToken);
            await lifecycle.OnStop(TestContext.Current.CancellationToken);

            Assert.Equal(0, workingSet.Count);
            Assert.DoesNotContain(member, workingSet.Members);
            Assert.Equal(totalVisits, visits);
            observer.Received(1).OnAdded(member);
            observer.Received(reactivationCycles + 1).OnIdle(member);
            observer.Received(reactivationCycles).OnActive(member);
            observer.Received(1).OnEvicted(member);
        }

        [Fact, TestCategory("Activation")]
        public async Task WorkingSetMembers_EnumerationToleratesConcurrentRemoveAndReadd()
        {
            var timer = Substitute.For<IAsyncTimer>();
            timer.NextTick().Returns(Task.FromResult(false));
            var timerFactory = Substitute.For<IAsyncTimerFactory>();
            timerFactory.Create(Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<TimeProvider>()).Returns(timer);
            var observer = Substitute.For<IActivationWorkingSetObserver>();
            var firstEviction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var resumeWriter = new ManualResetEventSlim();
            var evictionCount = 0;
            observer.When(static observer => observer.OnEvicted(Arg.Any<IActivationWorkingSetMember>())).Do(_ =>
            {
                if (Interlocked.Increment(ref evictionCount) == 1)
                {
                    firstEviction.TrySetResult();
                    resumeWriter.Wait();
                }
            });
            var workingSet = new ActivationWorkingSet(
                timerFactory,
                NullLogger<ActivationWorkingSet>.Instance,
                [observer],
                CreateCatalogInstruments(),
                TimeProvider.System);
            var members = Enumerable.Range(0, 128).Select(_ => new TestWorkingSetMember()).ToArray();
            foreach (var member in members)
            {
                workingSet.OnActivated(member);
            }

            using var enumerator = workingSet.Members.GetEnumerator();
            Assert.True(enumerator.MoveNext());
            var enumeratedMembers = new List<IActivationWorkingSetMember> { enumerator.Current };
            var writer = Task.Run(() =>
            {
                foreach (var member in members)
                {
                    workingSet.OnEvicted(member);
                    workingSet.OnActivated(member);
                }
            }, TestContext.Current.CancellationToken);

            try
            {
                await firstEviction.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                while (enumerator.MoveNext())
                {
                    enumeratedMembers.Add(enumerator.Current);
                }
            }
            finally
            {
                resumeWriter.Set();
                await ObserveCleanup(writer);
            }

            Assert.All(enumeratedMembers, member => Assert.Contains(member, members));
            Assert.Equal(members.Length, workingSet.Count);
            Assert.True(members.Cast<IActivationWorkingSetMember>().ToHashSet().SetEquals(workingSet.Members));
            observer.Received(members.Length * 2).OnAdded(Arg.Any<IActivationWorkingSetMember>());
            observer.Received(members.Length).OnEvicted(Arg.Any<IActivationWorkingSetMember>());
        }

        [Fact, TestCategory("Activation")]
        public void WorkingSetMembers_ExcludesUnregisteredMember()
        {
            var timer = Substitute.For<IAsyncTimer>();
            timer.NextTick().Returns(Task.FromResult(false));
            var timerFactory = Substitute.For<IAsyncTimerFactory>();
            timerFactory.Create(Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<TimeProvider>()).Returns(timer);
            var workingSet = new ActivationWorkingSet(
                timerFactory,
                NullLogger<ActivationWorkingSet>.Instance,
                Array.Empty<IActivationWorkingSetObserver>(),
                CreateCatalogInstruments(),
                TimeProvider.System);
            var member = new TestWorkingSetMember();
            workingSet.OnActivated(member);
            lock (member)
            {
                member.IsInWorkingSet = false;
            }

            Assert.Empty(workingSet.Members);
        }

        [Fact, TestCategory("Activation")]
        public async Task WorkingSetScan_SerializesRemovalWithReactivation()
        {
            var timer = Substitute.For<IAsyncTimer>();
            timer.NextTick().Returns(Task.FromResult(true), Task.FromResult(true), Task.FromResult(false));
            var timerFactory = Substitute.For<IAsyncTimerFactory>();
            timerFactory.Create(Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<TimeProvider>()).Returns(timer);
            var observer = Substitute.For<IActivationWorkingSetObserver>();
            var workingSet = new ActivationWorkingSet(
                timerFactory,
                NullLogger<ActivationWorkingSet>.Instance,
                [observer],
                CreateCatalogInstruments(),
                TimeProvider.System);
            var removalScanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var resumeScan = new ManualResetEventSlim();
            var member = new TestWorkingSetMember(wouldRemove =>
            {
                if (!wouldRemove)
                {
                    return true;
                }

                removalScanStarted.TrySetResult();
                resumeScan.Wait();
                return true;
            });
            workingSet.OnActivated(member);

            var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
            ((ILifecycleParticipant<ISiloLifecycle>)workingSet).Participate(lifecycle);
            var reactivationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? reactivationTask = null;
            Task? stopTask = null;
            var lifecycleStarted = false;
            try
            {
                await lifecycle.OnStart(TestContext.Current.CancellationToken);
                lifecycleStarted = true;
                await removalScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                reactivationTask = Task.Run(() =>
                {
                    reactivationStarted.SetResult();
                    workingSet.OnActive(member);
                }, TestContext.Current.CancellationToken);
                await reactivationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.False(reactivationTask.IsCompleted);
                resumeScan.Set();
                await reactivationTask;
                stopTask = lifecycle.OnStop(TestContext.Current.CancellationToken);
                await stopTask;
            }
            finally
            {
                resumeScan.Set();
                if (reactivationTask is not null)
                {
                    await ObserveCleanup(reactivationTask);
                }

                if (lifecycleStarted)
                {
                    stopTask ??= lifecycle.OnStop(CancellationToken.None);
                    await ObserveCleanup(stopTask);
                }
            }

            Assert.Equal(1, workingSet.Count);
            Assert.Contains(member, workingSet.Members);
            observer.Received(1).OnAdded(member);
            observer.Received(1).OnIdle(member);
            observer.Received(1).OnEvicted(member);
            observer.Received(1).OnActive(member);
        }

        [Fact, TestCategory("Activation")]
        public void WorkingSetScan_SkipsRemovedMember()
        {
            var timer = Substitute.For<IAsyncTimer>();
            var timerFactory = Substitute.For<IAsyncTimerFactory>();
            timerFactory.Create(Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<TimeProvider>()).Returns(timer);
            var observer = Substitute.For<IActivationWorkingSetObserver>();
            var workingSet = new ActivationWorkingSet(
                timerFactory,
                NullLogger<ActivationWorkingSet>.Instance,
                [observer],
                CreateCatalogInstruments(),
                TimeProvider.System);
            var visits = 0;
            var member = new TestWorkingSetMember(_ =>
            {
                visits++;
                return true;
            });
            workingSet.OnActivated(member);
            workingSet.OnEvicted(member);

            var visitMember = typeof(ActivationWorkingSet).GetMethod(
                "VisitMember",
                BindingFlags.Instance | BindingFlags.NonPublic,
                [typeof(IActivationWorkingSetMember)])
                ?? throw new InvalidOperationException("Could not find the working-set scan method.");
            visitMember.Invoke(workingSet, [member]);

            Assert.Equal(0, visits);
            Assert.Equal(0, workingSet.Count);
            observer.Received(1).OnAdded(member);
            observer.Received(1).OnEvicted(member);
            observer.DidNotReceive().OnIdle(member);
            observer.DidNotReceive().OnActive(member);
        }

        [Fact, TestCategory("Activation")]
        public void WorkingSet_PublicMemberUsesDictionaryBackedClockState()
        {
            var timer = Substitute.For<IAsyncTimer>();
            var timerFactory = Substitute.For<IAsyncTimerFactory>();
            timerFactory.Create(Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<TimeProvider>()).Returns(timer);
            var observer = Substitute.For<IActivationWorkingSetObserver>();
            var workingSet = new ActivationWorkingSet(
                timerFactory,
                NullLogger<ActivationWorkingSet>.Instance,
                [observer],
                CreateCatalogInstruments(),
                TimeProvider.System);
            var member = new PublicWorkingSetMember();
            var visitMember = typeof(ActivationWorkingSet).GetMethod(
                "VisitMember",
                BindingFlags.Instance | BindingFlags.NonPublic,
                [typeof(IActivationWorkingSetMember)])
                ?? throw new InvalidOperationException("Could not find the working-set scan method.");

            workingSet.OnActivated(member);
            visitMember.Invoke(workingSet, [member]);
            visitMember.Invoke(workingSet, [member]);

            Assert.Equal([false, true], member.CandidateCalls);
            Assert.Equal(0, workingSet.Count);
            Assert.Empty(workingSet.Members);
            observer.Received(1).OnIdle(member);
            observer.Received(1).OnEvicted(member);

            workingSet.OnActive(member);

            Assert.Equal(1, workingSet.Count);
            Assert.Equal([member], workingSet.Members);
            observer.Received(1).OnActive(member);
        }

        private IActivationWorkingSetMember PrepareActivation(int collectionAgeLimitMinutes, ActivationCollector collector)
            => PrepareActivation(TimeSpan.FromMinutes(collectionAgeLimitMinutes), collector);

        private static async Task ObserveCleanup(Task task)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch (OperationCanceledException) when (task.IsCanceled)
            {
            }
        }

        private static CatalogInstruments CreateCatalogInstruments()
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            services.AddSingleton<OrleansInstruments>();
            services.AddSingleton<CatalogInstruments>();
            return services.BuildServiceProvider().GetRequiredService<CatalogInstruments>();
        }

        private static void ConfigureCollectionRegistrationSlot(ICollectibleGrainContext activation)
        {
            IActivationCollectionRegistration? registration = null;
            activation.CollectionRegistration.Returns(_ => Volatile.Read(ref registration));
            activation.GetOrSetCollectionRegistration(Arg.Any<IActivationCollectionRegistration>())
                .Returns(call =>
                {
                    var candidate = call.Arg<IActivationCollectionRegistration>();
                    return Interlocked.CompareExchange(ref registration, candidate, null) ?? candidate;
                });
        }

        private IActivationWorkingSetMember PrepareActivation(TimeSpan collectionAgeLimit, ActivationCollector collector)
        {
            var activation = Substitute.For<ICollectibleGrainContext, IActivationWorkingSetMember>();
            ConfigureCollectionRegistrationSlot(activation);
            activation.CollectionAgeLimit.Returns(collectionAgeLimit);
            activation.IsExemptFromCollection.Returns(false);
            activation.TryDeactivateForCollection(
                    Arg.Any<DeactivationReason>(),
                    Arg.Any<DateTime>(),
                    Arg.Any<TimeSpan>(),
                    Arg.Any<bool>(),
                    Arg.Any<CancellationToken>())
                .Returns(ActivationCollectionResult.StartedDeactivation);
            activation.Deactivated.Returns(Task.CompletedTask).AndDoes(_ => { Interlocked.Decrement(ref collector._activationCount); });

            return (IActivationWorkingSetMember)activation;
        }

        private sealed class TestWorkingSetMember(Func<bool, bool>? isCandidateForRemoval = null) : IActivationWorkingSetMemberStatus
        {
            private bool _isIdle;
            private bool _isInWorkingSet;

            public bool IsIdle
            {
                get => Volatile.Read(ref _isIdle);
                set => Volatile.Write(ref _isIdle, value);
            }

            public bool IsInWorkingSet
            {
                get => Volatile.Read(ref _isInWorkingSet);
                set => Volatile.Write(ref _isInWorkingSet, value);
            }

            public bool WasRemovedByCollection { get; set; }

            public bool IsCandidateForRemoval(bool wouldRemove)
                => isCandidateForRemoval?.Invoke(wouldRemove) ?? false;

        }

        private sealed class PublicWorkingSetMember : IActivationWorkingSetMember
        {
            public List<bool> CandidateCalls { get; } = [];

            public bool IsCandidateForRemoval(bool wouldRemove)
            {
                CandidateCalls.Add(wouldRemove);
                return true;
            }
        }

        [Fact, TestCategory("Activation")]
        public void WorkingSet_SequentialGeneratedTrace_MatchesReferenceModel()
        {
            var generatedOperation = CsCheck.Gen.Select(
                CsCheck.Gen.Int[0, 7],
                CsCheck.Gen.Int[0, 3],
                CsCheck.Gen.Bool,
                static (kind, memberId, candidateEligible) =>
                    new WorkingSetOperation(0, (WorkingSetOperationKind)kind, memberId, candidateEligible));
            var traceGenerator = CsCheck.Gen.SelectMany(
                CsCheck.Gen.Int[0, 64],
                length => CsCheck.Gen.Select(
                    generatedOperation.Array[length],
                    static generated => GetWorkingSetCoverageSpine()
                        .Concat(generated)
                        .Select(static (operation, index) => operation with { Index = index })
                        .ToArray()));

            CsCheck.Check.Sample(
                traceGenerator,
                RunSequentialWorkingSetTrace,
                seed: "0N0XIzNsQ0O2",
                iter: 100,
                threads: 1,
                print: FormatWorkingSetTrace);
        }

        [Fact, TestCategory("Activation")]
        public async Task WorkingSet_ConcurrentOnActiveForAbsentMember_AddsOnceAndNotifiesEveryCaller()
        {
            const int workerCount = 8;
            await using var harness = await WorkingSetHarness.CreateAsync();
            var member = harness.Members[0];
            using var ready = new CountdownEvent(workerCount);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workers = Enumerable.Range(0, workerCount).Select(worker => Task.Run(async () =>
            {
                ready.Signal();
                await start.Task;
                harness.WorkingSet.OnActive(member);
            }, TestContext.Current.CancellationToken)).ToArray();

            try
            {
                Assert.True(
                    ready.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
                    "Workers did not reach the OnActive start gate.");
                start.TrySetResult();
                await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
            finally
            {
                start.TrySetResult();
                await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }

            Assert.Equal(1, harness.WorkingSet.Count);
            Assert.True(member.IsInWorkingSet);
            Assert.False(member.IsIdle);
            Assert.Same(member, Assert.Single(harness.WorkingSet.Members));
            Assert.Equal(Enumerable.Repeat("Active", workerCount), harness.Observer.GetHistory(0));
            Assert.Equal(0, harness.Observer.Count("Added"));
            Assert.Equal(workerCount, harness.Observer.Count("Active"));
            Assert.Equal(0, harness.Observer.Count("Idle"));
            Assert.Equal(0, harness.Observer.Count("Evicted"));
            Assert.Equal(0, harness.Observer.Count("Deactivating"));
            Assert.Equal(0, harness.Observer.Count("Deactivated"));
        }

        [Fact, TestCategory("Activation")]
        public async Task WorkingSet_BoundedConcurrentTransitionsPreserveMembershipCount()
        {
            const int workerCount = 8;
            const int operationsPerWorker = 10_000;
            await using var harness = await WorkingSetHarness.CreateAsync();
            var visitMember = typeof(ActivationWorkingSet).GetMethod(
                "VisitMember",
                BindingFlags.Instance | BindingFlags.NonPublic,
                [typeof(IActivationWorkingSetMember)])
                ?? throw new InvalidOperationException("Could not find the working-set scan method.");
            foreach (var member in harness.Members)
            {
                harness.WorkingSet.OnActivated(member);
            }

            var workers = Enumerable.Range(0, workerCount).Select(worker => Task.Run(() =>
            {
                var random = new Random(42 + worker);
                for (var i = 0; i < operationsPerWorker; i++)
                {
                    var memberId = random.Next(harness.Members.Count);
                    var member = harness.Members[memberId];
                    switch (random.Next(4))
                    {
                        case 0:
                            harness.WorkingSet.OnActive(member);
                            break;
                        case 1:
                            harness.WorkingSet.OnEvicted(member);
                            break;
                        case 2:
                            harness.MemberStates[memberId].CandidateEligible = random.Next(2) == 0;
                            visitMember.Invoke(harness.WorkingSet, [member]);
                            break;
                        default:
                            harness.WorkingSet.OnActive(member);
                            visitMember.Invoke(harness.WorkingSet, [member]);
                            break;
                    }
                }
            }, TestContext.Current.CancellationToken)).ToArray();

            await Task.WhenAll(workers);

            foreach (var memberState in harness.MemberStates)
            {
                memberState.CandidateEligible = false;
            }

            foreach (var member in harness.Members)
            {
                harness.WorkingSet.OnActive(member);
            }

            Assert.Equal(harness.Members.Count, harness.WorkingSet.Count);
            Assert.True(harness.Members.Cast<IActivationWorkingSetMember>().ToHashSet().SetEquals(harness.WorkingSet.Members));
            Assert.All(harness.Members, member =>
            {
                Assert.True(member.IsInWorkingSet);
                Assert.False(member.IsIdle);
            });
        }

        [Fact, TestCategory("Activation")]
        public async Task WorkingSet_EvictionCallbackCanOverlapReAddWithoutHoldingMemberLock()
        {
            await using var harness = await WorkingSetHarness.CreateAsync();
            var member = harness.Members[0];
            harness.WorkingSet.OnActivated(member);
            harness.Observer.Clear();
            var gate = harness.Observer.ArmEvictionGate(0);
            var eviction = Task.Run(
                () => harness.WorkingSet.OnEvicted(member),
                TestContext.Current.CancellationToken);
            Task? reAdd = null;
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                reAdd = Task.Run(
                    () => harness.WorkingSet.OnActive(member),
                    TestContext.Current.CancellationToken);
                await reAdd.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.Equal(1, harness.WorkingSet.Count);
                Assert.True(member.IsInWorkingSet);
                Assert.False(member.IsIdle);
                Assert.Same(member, Assert.Single(harness.WorkingSet.Members));
                Assert.Equal(["EvictedStarted", "Active"], harness.Observer.GetHistory(0));
            }
            finally
            {
                gate.Release.TrySetResult();
                if (reAdd is not null)
                {
                    await reAdd.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                }

                await eviction.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }

            Assert.Equal(["EvictedStarted", "Active", "EvictedCompleted"], harness.Observer.GetHistory(0));
            Assert.Equal(0, harness.Observer.Count("Added"));
            Assert.Equal(1, harness.WorkingSet.Count);
            Assert.True(member.IsInWorkingSet);
            Assert.False(member.IsIdle);
            Assert.Same(member, Assert.Single(harness.WorkingSet.Members));
        }

        private static WorkingSetOperation[] GetWorkingSetCoverageSpine() =>
        [
            new(0, WorkingSetOperationKind.Activate, 0, false),
            new(0, WorkingSetOperationKind.Active, 0, false),
            new(0, WorkingSetOperationKind.SetCandidate, 0, false),
            new(0, WorkingSetOperationKind.Scan, 0, false),
            new(0, WorkingSetOperationKind.SetCandidate, 0, true),
            new(0, WorkingSetOperationKind.Scan, 0, false),
            new(0, WorkingSetOperationKind.Scan, 0, false),
            new(0, WorkingSetOperationKind.Activate, 1, false),
            new(0, WorkingSetOperationKind.Evict, 1, false),
            new(0, WorkingSetOperationKind.Activate, 1, false),
            new(0, WorkingSetOperationKind.Deactivating, 1, false),
            new(0, WorkingSetOperationKind.Deactivated, 1, false),
            new(0, WorkingSetOperationKind.Active, 2, false),
            new(0, WorkingSetOperationKind.DeactivatePair, 2, false),
            new(0, WorkingSetOperationKind.Activate, 3, false),
            new(0, WorkingSetOperationKind.Evict, 3, false)
        ];

        private static string FormatWorkingSetTrace(WorkingSetOperation[] trace)
            => string.Join(Environment.NewLine, trace.Select(static operation => operation.ToString()));

        private static void RunSequentialWorkingSetTrace(WorkingSetOperation[] trace)
        {
            var harness = WorkingSetHarness.CreateAsync().GetAwaiter().GetResult();
            try
            {
                var model = new WorkingSetReferenceModel(harness.Members.Count);
                model.AssertMatches(harness);
                foreach (var operation in trace)
                {
                    var expectedDuplicate = model.Apply(operation);
                    var actualDuplicate = ExecuteWorkingSetOperation(harness, operation, expectedDuplicate);
                    Assert.Equal(expectedDuplicate, actualDuplicate);
                    model.AssertMatches(harness);
                }
            }
            finally
            {
                harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        private static bool ExecuteWorkingSetOperation(
            WorkingSetHarness harness,
            WorkingSetOperation operation,
            bool expectedDuplicate)
        {
            var member = harness.Members[operation.MemberId];
            switch (operation.Kind)
            {
                case WorkingSetOperationKind.Activate:
                    {
                        var exception = Record.Exception(() => harness.WorkingSet.OnActivated(member));
                        if (expectedDuplicate)
                        {
                            Assert.IsType<InvalidOperationException>(exception);
                        }
                        else if (exception is not null)
                        {
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
                        }

                        return exception is not null;
                    }
                case WorkingSetOperationKind.Active:
                    harness.WorkingSet.OnActive(member);
                    break;
                case WorkingSetOperationKind.SetCandidate:
                    harness.MemberStates[operation.MemberId].CandidateEligible = operation.CandidateEligible;
                    break;
                case WorkingSetOperationKind.Scan:
                    harness.ScanOnceAsync().GetAwaiter().GetResult();
                    break;
                case WorkingSetOperationKind.Evict:
                    harness.WorkingSet.OnEvicted(member);
                    break;
                case WorkingSetOperationKind.Deactivating:
                    harness.WorkingSet.OnDeactivating(member);
                    break;
                case WorkingSetOperationKind.Deactivated:
                    harness.WorkingSet.OnDeactivated(member);
                    break;
                case WorkingSetOperationKind.DeactivatePair:
                    harness.WorkingSet.OnDeactivating(member);
                    harness.WorkingSet.OnDeactivated(member);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }

            return false;
        }

        private enum WorkingSetOperationKind
        {
            Activate,
            Active,
            SetCandidate,
            Scan,
            Evict,
            Deactivating,
            Deactivated,
            DeactivatePair
        }

        private readonly record struct WorkingSetOperation(
            int Index,
            WorkingSetOperationKind Kind,
            int MemberId,
            bool CandidateEligible)
        {
            public override string ToString()
                => $"#{Index}: {Kind}(member={MemberId}, candidateEligible={CandidateEligible})";
        }

        private sealed class WorkingSetMemberState
        {
            private readonly ConcurrentQueue<bool> _candidateCalls = new();
            private int _candidateEligible;

            public bool CandidateEligible
            {
                get => Volatile.Read(ref _candidateEligible) != 0;
                set => Volatile.Write(ref _candidateEligible, value ? 1 : 0);
            }

            public bool IsCandidateForRemoval(bool wouldRemove)
            {
                _candidateCalls.Enqueue(wouldRemove);
                return CandidateEligible;
            }

            public bool[] GetCandidateCalls() => _candidateCalls.ToArray();
        }

        private sealed class RecordingWorkingSetObserver(
            Func<IActivationWorkingSetMember, int> getMemberId,
            int memberCount) : IActivationWorkingSetObserver
        {
            private readonly ConcurrentQueue<string>[] _history =
                Enumerable.Range(0, memberCount).Select(static _ => new ConcurrentQueue<string>()).ToArray();
            private readonly object _gateLock = new();
            private int _gatedMemberId = -1;
            private TaskCompletionSource? _evictionEntered;
            private TaskCompletionSource? _evictionRelease;

            public void OnAdded(IActivationWorkingSetMember member) => Record(member, "Added");

            public void OnActive(IActivationWorkingSetMember member) => Record(member, "Active");

            public void OnIdle(IActivationWorkingSetMember member) => Record(member, "Idle");

            public void OnEvicted(IActivationWorkingSetMember member)
            {
                var memberId = getMemberId(member);
                TaskCompletionSource? entered;
                TaskCompletionSource? release;
                lock (_gateLock)
                {
                    entered = memberId == _gatedMemberId ? _evictionEntered : null;
                    release = memberId == _gatedMemberId ? _evictionRelease : null;
                }

                if (entered is null || release is null)
                {
                    Record(memberId, "Evicted");
                    return;
                }

                Record(memberId, "EvictedStarted");
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                Record(memberId, "EvictedCompleted");
            }

            public void OnDeactivating(IActivationWorkingSetMember member) => Record(member, "Deactivating");

            public void OnDeactivated(IActivationWorkingSetMember member) => Record(member, "Deactivated");

            public (TaskCompletionSource Entered, TaskCompletionSource Release) ArmEvictionGate(int memberId)
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gateLock)
                {
                    _gatedMemberId = memberId;
                    _evictionEntered = entered;
                    _evictionRelease = release;
                }

                return (entered, release);
            }

            public string[] GetHistory(int memberId) => _history[memberId].ToArray();

            public int Count(string eventName)
                => _history.Sum(history => history.Count(item => string.Equals(item, eventName, StringComparison.Ordinal)));

            public void Clear()
            {
                foreach (var history in _history)
                {
                    while (history.TryDequeue(out _))
                    {
                    }
                }
            }

            private void Record(IActivationWorkingSetMember member, string eventName)
                => Record(getMemberId(member), eventName);

            private void Record(int memberId, string eventName) => _history[memberId].Enqueue(eventName);
        }

        private sealed class WorkingSetReferenceModel
        {
            private readonly ModelMember[] _members;

            public WorkingSetReferenceModel(int memberCount)
            {
                _members = Enumerable.Range(0, memberCount).Select(static _ => new ModelMember()).ToArray();
            }

            public bool Apply(WorkingSetOperation operation)
            {
                var member = _members[operation.MemberId];
                switch (operation.Kind)
                {
                    case WorkingSetOperationKind.Activate:
                        if (member.Present)
                        {
                            return true;
                        }

                        member.Present = true;
                        member.IsInWorkingSet = true;
                        member.IsIdle = false;
                        member.History.Add("Added");
                        break;
                    case WorkingSetOperationKind.Active:
                        member.Present = true;
                        member.IsInWorkingSet = true;
                        member.IsIdle = false;
                        member.History.Add("Active");
                        break;
                    case WorkingSetOperationKind.SetCandidate:
                        member.CandidateEligible = operation.CandidateEligible;
                        break;
                    case WorkingSetOperationKind.Scan:
                        foreach (var scanMember in _members)
                        {
                            Scan(scanMember);
                        }

                        break;
                    case WorkingSetOperationKind.Evict:
                        Evict(member);
                        break;
                    case WorkingSetOperationKind.Deactivating:
                        Evict(member);
                        member.History.Add("Deactivating");
                        break;
                    case WorkingSetOperationKind.Deactivated:
                        Evict(member);
                        member.History.Add("Deactivated");
                        break;
                    case WorkingSetOperationKind.DeactivatePair:
                        Evict(member);
                        member.History.Add("Deactivating");
                        Evict(member);
                        member.History.Add("Deactivated");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(operation));
                }

                return false;
            }

            public void AssertMatches(WorkingSetHarness harness)
            {
                Assert.Equal(_members.Count(static member => member.Present), harness.WorkingSet.Count);
                var expectedVisibleMembers = _members
                    .Select(static (member, id) => (member, id))
                    .Where(static item => item.member.Present && !item.member.IsIdle)
                    .Select(static item => item.id)
                    .ToArray();
                var actualVisibleMembers = harness.WorkingSet.Members
                    .Select(harness.GetMemberId)
                    .Order()
                    .ToArray();
                Assert.Equal(expectedVisibleMembers, actualVisibleMembers);

                for (var memberId = 0; memberId < _members.Length; memberId++)
                {
                    var expected = _members[memberId];
                    var actual = harness.Members[memberId];
                    Assert.Equal(expected.IsInWorkingSet, actual.IsInWorkingSet);
                    Assert.Equal(expected.IsIdle, actual.IsIdle);
                    Assert.Equal(expected.CandidateCalls, harness.MemberStates[memberId].GetCandidateCalls());
                    Assert.Equal(expected.History, harness.Observer.GetHistory(memberId));
                }
            }

            private static void Scan(ModelMember member)
            {
                if (!member.IsInWorkingSet)
                {
                    return;
                }

                var wouldRemove = member.IsIdle;
                member.CandidateCalls.Add(wouldRemove);
                if (!member.CandidateEligible)
                {
                    member.IsIdle = false;
                    member.History.Add("Active");
                }
                else if (!wouldRemove)
                {
                    member.IsIdle = true;
                    member.History.Add("Idle");
                }
                else if (member.Present)
                {
                    member.Present = false;
                    member.IsInWorkingSet = false;
                    member.IsIdle = false;
                    member.History.Add("Evicted");
                }
            }

            private static void Evict(ModelMember member)
            {
                if (!member.Present)
                {
                    return;
                }

                member.Present = false;
                member.IsInWorkingSet = false;
                member.IsIdle = false;
                member.History.Add("Evicted");
            }

            private sealed class ModelMember
            {
                public bool Present { get; set; }
                public bool IsInWorkingSet { get; set; }
                public bool IsIdle { get; set; }
                public bool CandidateEligible { get; set; }
                public List<bool> CandidateCalls { get; } = [];
                public List<string> History { get; } = [];
            }
        }

        private sealed class WorkingSetHarness : IAsyncDisposable
        {
            private readonly ServiceProvider _serviceProvider;
            private readonly ControlledAsyncTimer _timer;
            private readonly SiloLifecycleSubject _lifecycle;

            private WorkingSetHarness()
            {
                var services = new ServiceCollection();
                services.AddMetrics();
                services.AddSingleton<OrleansInstruments>();
                services.AddSingleton<CatalogInstruments>();
                _serviceProvider = services.BuildServiceProvider();
                MemberStates = Enumerable.Range(0, 4).Select(static _ => new WorkingSetMemberState()).ToArray();
                Members = MemberStates
                    .Select(state => new TestWorkingSetMember(state.IsCandidateForRemoval))
                    .ToArray();
                var memberIds = Members
                    .Select(static (member, id) => (member, id))
                    .ToDictionary(static item => (IActivationWorkingSetMember)item.member, static item => item.id);
                GetMemberId = member => memberIds[member];
                Observer = new RecordingWorkingSetObserver(GetMemberId, Members.Count);
                _timer = new ControlledAsyncTimer();
                var timerFactory = Substitute.For<IAsyncTimerFactory>();
                timerFactory.Create(Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<TimeProvider>()).Returns(_timer);
                WorkingSet = new ActivationWorkingSet(
                    timerFactory,
                    NullLogger<ActivationWorkingSet>.Instance,
                    [Observer],
                    _serviceProvider.GetRequiredService<CatalogInstruments>(),
                    TimeProvider.System);
                _lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
                ((ILifecycleParticipant<ISiloLifecycle>)WorkingSet).Participate(_lifecycle);
            }

            public ActivationWorkingSet WorkingSet { get; }
            public IReadOnlyList<TestWorkingSetMember> Members { get; }
            public IReadOnlyList<WorkingSetMemberState> MemberStates { get; }
            public RecordingWorkingSetObserver Observer { get; }
            public Func<IActivationWorkingSetMember, int> GetMemberId { get; }
            public int TimerGeneration => _timer.Generation;

            public static async Task<WorkingSetHarness> CreateAsync()
            {
                var result = new WorkingSetHarness();
                try
                {
                    await result._lifecycle.OnStart(TestContext.Current.CancellationToken);
                    await result._timer.WaitForGenerationAsync(1);
                    return result;
                }
                catch
                {
                    await result.DisposeAsync();
                    throw;
                }
            }

            public async Task ScanOnceAsync()
            {
                var nextGeneration = _timer.Generation + 1;
                _timer.CompleteCurrent(result: true);
                await _timer.WaitForGenerationAsync(nextGeneration);
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    await _lifecycle.OnStop(TestContext.Current.CancellationToken)
                        .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                }
                finally
                {
                    await _serviceProvider.DisposeAsync();
                }
            }
        }

        private sealed class ControlledAsyncTimer : IAsyncTimer
        {
            private readonly object _lock = new();
            private TaskCompletionSource _generationChanged =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private TaskCompletionSource<bool>? _current;
            private bool _disposed;
            private int _generation;

            public int Generation
            {
                get
                {
                    lock (_lock)
                    {
                        return _generation;
                    }
                }
            }

            public Task<bool> NextTick(TimeSpan? overrideDelay = default)
            {
                TaskCompletionSource generationChanged;
                TaskCompletionSource<bool> current;
                lock (_lock)
                {
                    if (_disposed)
                    {
                        return Task.FromResult(false);
                    }

                    Assert.Null(_current);
                    current = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _current = current;
                    _generation++;
                    generationChanged = _generationChanged;
                    _generationChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                generationChanged.TrySetResult();
                return current.Task;
            }

            public void CompleteCurrent(bool result)
            {
                TaskCompletionSource<bool> current;
                lock (_lock)
                {
                    current = _current ?? throw new InvalidOperationException("The working-set timer is not awaiting a tick.");
                    _current = null;
                }

                current.TrySetResult(result);
            }

            public async Task WaitForGenerationAsync(int expectedGeneration)
            {
                while (true)
                {
                    Task generationChanged;
                    lock (_lock)
                    {
                        if (_generation >= expectedGeneration)
                        {
                            return;
                        }

                        generationChanged = _generationChanged.Task;
                    }

                    await generationChanged.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                }
            }

            public bool CheckHealth(DateTime lastCheckTime, [NotNullWhen(false)] out string? reason)
            {
                reason = null;
                return true;
            }

            public void Dispose()
            {
                TaskCompletionSource<bool>? current;
                lock (_lock)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    current = _current;
                    _current = null;
                }

                current?.TrySetResult(false);
            }
        }

        [Fact, TestCategory("Activation")]
        public void ActivationData_Constructor_InitializesWorkingSetClockStatus()
        {
            using var fixture = new ActivationDataWorkingSetFixture();

            Assert.Equal(ActivationState.Creating, fixture.Activation.State);
            Assert.True(fixture.Member.IsInWorkingSet);
            Assert.False(fixture.Member.IsIdle);
            Assert.False(fixture.WasRemovedByCollection);
        }

        [Theory, TestCategory("Activation")]
        [MemberData(nameof(ActivationStatusCases))]
        public void ActivationStatus_PackedFieldsPreserveIndependentBits(
            int stateValue,
            bool expectedInWorkingSet,
            bool expectedIdle)
        {
            using var fixture = new ActivationDataWorkingSetFixture();
            var expectedState = (ActivationState)stateValue;
            var otherState = expectedState == ActivationState.Invalid
                ? ActivationState.Creating
                : ActivationState.Invalid;

            lock (fixture.SyncRoot)
            {
                fixture.Member.IsInWorkingSet = expectedInWorkingSet;
                fixture.Member.IsIdle = expectedIdle;
                fixture.SetState(otherState);
            }

            Assert.Equal(otherState, fixture.Activation.State);
            Assert.Equal(expectedInWorkingSet, fixture.Member.IsInWorkingSet);
            Assert.Equal(expectedIdle, fixture.Member.IsIdle);
            Assert.False(fixture.WasRemovedByCollection);

            lock (fixture.SyncRoot)
            {
                fixture.SetState(expectedState);
            }

            Assert.Equal(expectedState, fixture.Activation.State);
            Assert.Equal(expectedInWorkingSet, fixture.Member.IsInWorkingSet);
            Assert.Equal(expectedIdle, fixture.Member.IsIdle);
            Assert.False(fixture.WasRemovedByCollection);

            lock (fixture.SyncRoot)
            {
                fixture.Member.IsInWorkingSet = !expectedInWorkingSet;
            }

            Assert.Equal(expectedState, fixture.Activation.State);
            Assert.Equal(!expectedInWorkingSet, fixture.Member.IsInWorkingSet);
            Assert.Equal(expectedIdle, fixture.Member.IsIdle);
            Assert.False(fixture.WasRemovedByCollection);

            lock (fixture.SyncRoot)
            {
                fixture.Member.IsInWorkingSet = expectedInWorkingSet;
                fixture.Member.IsIdle = !expectedIdle;
            }

            Assert.Equal(expectedState, fixture.Activation.State);
            Assert.Equal(expectedInWorkingSet, fixture.Member.IsInWorkingSet);
            Assert.Equal(!expectedIdle, fixture.Member.IsIdle);
            Assert.False(fixture.WasRemovedByCollection);

            lock (fixture.SyncRoot)
            {
                fixture.Member.IsIdle = expectedIdle;
            }

            Assert.Equal(expectedState, fixture.Activation.State);
            Assert.Equal(expectedInWorkingSet, fixture.Member.IsInWorkingSet);
            Assert.Equal(expectedIdle, fixture.Member.IsIdle);
            Assert.False(fixture.WasRemovedByCollection);
        }

        [Fact, TestCategory("Activation")]
        public void ActivationData_CollectionCandidateMarker_RequiresSuccessfulRemoval()
        {
            using var fixture = new ActivationDataWorkingSetFixture();

            fixture.AdvanceIdleDurationTo(10_000);
            bool isCandidateAtBoundary;
            lock (fixture.SyncRoot)
            {
                isCandidateAtBoundary = fixture.Member.IsCandidateForRemoval(wouldRemove: true);
            }

            Assert.False(isCandidateAtBoundary);
            Assert.False(fixture.WasRemovedByCollection);

            fixture.AdvanceIdleDurationTo(10_001);
            bool isCandidateOnFirstPass;
            lock (fixture.SyncRoot)
            {
                isCandidateOnFirstPass = fixture.Member.IsCandidateForRemoval(wouldRemove: false);
            }

            Assert.True(isCandidateOnFirstPass);
            Assert.False(fixture.WasRemovedByCollection);

            bool isCandidateOnRemovalPass;
            lock (fixture.SyncRoot)
            {
                isCandidateOnRemovalPass = fixture.Member.IsCandidateForRemoval(wouldRemove: true);
            }

            Assert.True(isCandidateOnRemovalPass);
            Assert.False(fixture.WasRemovedByCollection);

            lock (fixture.SyncRoot)
            {
                fixture.SetState(ActivationState.Valid);
                fixture.Member.IsIdle = true;
            }

            Assert.Equal(ActivationState.Valid, fixture.Activation.State);
            Assert.True(fixture.Member.IsInWorkingSet);
            Assert.True(fixture.Member.IsIdle);
            Assert.False(fixture.WasRemovedByCollection);
        }

        [Fact, TestCategory("Activation")]
        public void ActivationData_ClockCollectionThenOnActive_ClearsCollectionMarker()
        {
            using var fixture = new ActivationDataWorkingSetFixture();
            lock (fixture.SyncRoot)
            {
                fixture.SetState(ActivationState.Valid);
            }

            fixture.WorkingSet.OnActivated(fixture.Member);
            fixture.AdvanceIdleDurationTo(10_001);

            fixture.ScanOnce();

            Assert.Equal(1, fixture.WorkingSet.Count);
            Assert.True(fixture.Member.IsInWorkingSet);
            Assert.True(fixture.Member.IsIdle);
            Assert.False(fixture.WasRemovedByCollection);
            Assert.Empty(fixture.WorkingSet.Members);
            Assert.Equal(["Added", "Idle"], fixture.Observer.GetHistory(0));

            fixture.ScanOnce();

            Assert.Equal(0, fixture.WorkingSet.Count);
            Assert.False(fixture.Member.IsInWorkingSet);
            Assert.False(fixture.Member.IsIdle);
            Assert.True(fixture.WasRemovedByCollection);
            Assert.Empty(fixture.WorkingSet.Members);
            Assert.Equal(["Added", "Idle", "Evicted"], fixture.Observer.GetHistory(0));

            fixture.WorkingSet.OnActive(fixture.Member);

            Assert.Equal(1, fixture.WorkingSet.Count);
            Assert.True(fixture.Member.IsInWorkingSet);
            Assert.False(fixture.Member.IsIdle);
            Assert.False(fixture.WasRemovedByCollection);
            Assert.Equal([fixture.Member], fixture.WorkingSet.Members);
            Assert.Equal(["Added", "Idle", "Evicted", "Active"], fixture.Observer.GetHistory(0));
        }

        [Fact, TestCategory("Activation")]
        public void ActivationData_ExplicitWorkingSetDeactivation_DoesNotSetCollectionMarker()
        {
            using var fixture = new ActivationDataWorkingSetFixture();
            lock (fixture.SyncRoot)
            {
                fixture.SetState(ActivationState.Valid);
            }

            fixture.WorkingSet.OnActivated(fixture.Member);

            fixture.WorkingSet.OnDeactivating(fixture.Member);

            Assert.Equal(0, fixture.WorkingSet.Count);
            Assert.False(fixture.Member.IsInWorkingSet);
            Assert.False(fixture.Member.IsIdle);
            Assert.False(fixture.WasRemovedByCollection);
            Assert.Empty(fixture.WorkingSet.Members);
            Assert.Equal(["Added", "Evicted", "Deactivating"], fixture.Observer.GetHistory(0));
        }

        [Fact, TestCategory("Activation")]
        public void ActivationData_CompletedRequest_DoesNotReaddDeactivatingActivation()
        {
            using var fixture = new ActivationDataWorkingSetFixture();
            lock (fixture.SyncRoot)
            {
                fixture.SetState(ActivationState.Deactivating);
                fixture.Member.IsInWorkingSet = false;
                fixture.Member.IsIdle = false;
            }

            fixture.CompleteRequest(new Message());

            Assert.Equal(ActivationState.Deactivating, fixture.Activation.State);
            Assert.False(fixture.Member.IsInWorkingSet);
            Assert.False(fixture.Member.IsIdle);
            Assert.Equal(0, fixture.WorkingSet.Count);
            Assert.Empty(fixture.Observer.GetHistory(0));
        }

        public static IEnumerable<object[]> ActivationStatusCases()
        {
            foreach (var state in Enum.GetValues<ActivationState>())
            {
                yield return [(int)state, false, false];
                yield return [(int)state, false, true];
                yield return [(int)state, true, false];
                yield return [(int)state, true, true];
            }
        }

        private sealed class ActivationDataWorkingSetFixture : IDisposable
        {
            private static readonly PropertyInfo WasRemovedByCollectionProperty = typeof(ActivationData).GetProperty(
                "WasRemovedByCollection",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find the collection-removal marker.");
            private static readonly FieldInfo IdleDurationField = typeof(ActivationData).GetField(
                "_idleDuration",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find the activation idle-duration field.");
            private static readonly FieldInfo LockField = typeof(ActivationData).GetField(
                "_lock",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find the activation synchronization lock.");
            private static readonly FieldInfo ServiceScopeField = typeof(ActivationData).GetField(
                "_serviceScope",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find the activation service scope.");
            private static readonly FieldInfo SharedSchedulerLoggerField = typeof(GrainTypeSharedContext).GetField(
                "<SchedulerLogger>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find the shared scheduler logger.");
            private static readonly MethodInfo VisitMemberMethod = typeof(ActivationWorkingSet).GetMethod(
                "VisitMember",
                BindingFlags.Instance | BindingFlags.NonPublic,
                [typeof(IActivationWorkingSetMember)])
                ?? throw new InvalidOperationException("Could not find the working-set scan method.");
            private static readonly MethodInfo CompleteRequestMethod = typeof(ActivationData).GetMethod(
                "OnCompletedRequest",
                BindingFlags.Instance | BindingFlags.NonPublic,
                [typeof(Message)])
                ?? throw new InvalidOperationException("Could not find the completed-request method.");
            private static readonly MethodInfo SetStateMethod = typeof(ActivationData).GetMethod(
                "SetState",
                BindingFlags.Instance | BindingFlags.NonPublic,
                [typeof(ActivationState)])
                ?? throw new InvalidOperationException("Could not find the activation state transition method.");

            private readonly ServiceProvider _serviceProvider;
            private readonly IServiceScope _activationScope;
            private readonly ControlledAsyncTimer _timer;
            private long _elapsedMilliseconds;

            public ActivationDataWorkingSetFixture()
            {
                TimeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00.000+00:00"));
                var services = new ServiceCollection();
                services.AddOptions();
                services.AddLogging();
                services.AddMetrics();
                services.AddSingleton(TimeProvider);
                services.AddSingleton<System.TimeProvider>(TimeProvider);
                services.AddSingleton<OrleansInstruments>();
                services.AddSingleton<CatalogInstruments>();
                services.AddSingleton<SchedulerInstruments>();
                services.Configure<SchedulingOptions>(options =>
                {
                    options.DelayWarningThreshold = TimeSpan.FromMilliseconds(100);
                    options.ActivationSchedulingQuantum = TimeSpan.FromMilliseconds(100);
                    options.TurnWarningLengthThreshold = TimeSpan.FromMilliseconds(100);
                    options.StoppedActivationWarningInterval = TimeSpan.FromMilliseconds(200);
                });
                _serviceProvider = services.BuildServiceProvider();

                var address = GrainAddress.NewActivationAddress(
                    SiloAddress.New(System.Net.IPAddress.Loopback, 11_111, 1),
                    GrainId.Create("activation-working-set", "clock-fixture"));
                var shared = (GrainTypeSharedContext)RuntimeHelpers.GetUninitializedObject(typeof(GrainTypeSharedContext));
                SharedSchedulerLoggerField.SetValue(
                    shared,
                    _serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                        .CreateLogger(typeof(Orleans.Runtime.Scheduler.WorkItemGroup).FullName!));
                Activation = new ActivationData(
                    address,
                    context => new Orleans.Runtime.Scheduler.WorkItemGroup(
                        context,
                        _serviceProvider.GetRequiredService<IOptions<SchedulingOptions>>(),
                        _serviceProvider.GetRequiredService<SchedulerInstruments>()),
                    _serviceProvider,
                    shared);
                Member = Activation;
                _activationScope = (IServiceScope)ServiceScopeField.GetValue(Activation)!;

                Observer = new RecordingWorkingSetObserver(_ => 0, 1);
                _timer = new ControlledAsyncTimer();
                var timerFactory = Substitute.For<IAsyncTimerFactory>();
                timerFactory.Create(Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<System.TimeProvider>()).Returns(_timer);
                WorkingSet = new ActivationWorkingSet(
                    timerFactory,
                    NullLogger<ActivationWorkingSet>.Instance,
                    [Observer],
                    _serviceProvider.GetRequiredService<CatalogInstruments>(),
                    TimeProvider);
            }

            public ActivationData Activation { get; }
            public IActivationWorkingSetMemberStatus Member { get; }
#if NET10_0_OR_GREATER
            public Lock SyncRoot => (Lock)LockField.GetValue(Activation)!;
#else
            public object SyncRoot => LockField.GetValue(Activation)!;
#endif
            public FakeTimeProvider TimeProvider { get; }
            public ActivationWorkingSet WorkingSet { get; }
            public RecordingWorkingSetObserver Observer { get; }
            public bool WasRemovedByCollection
                => (bool)WasRemovedByCollectionProperty.GetValue(Activation)!;

            public void AdvanceIdleDurationTo(long elapsedMilliseconds)
            {
                var advance = elapsedMilliseconds - _elapsedMilliseconds;
                Assert.True(advance >= 0);
                TimeProvider.Advance(TimeSpan.FromMilliseconds(advance));
                lock (Activation)
                {
                    IdleDurationField.SetValue(
                        Activation,
                        CoarseStopwatch.FromTimestamp(0, elapsedMilliseconds));
                }

                _elapsedMilliseconds = elapsedMilliseconds;
                Assert.Equal(TimeSpan.FromMilliseconds(elapsedMilliseconds), Activation.GetIdleness());
            }

            public void ScanOnce() => VisitMemberMethod.Invoke(WorkingSet, [Member]);

            public void CompleteRequest(Message message) => CompleteRequestMethod.Invoke(Activation, [message]);

            public void SetState(ActivationState state) => SetStateMethod.Invoke(Activation, [state]);

            public void Dispose()
            {
                _timer.Dispose();
                _activationScope.Dispose();
                _serviceProvider.Dispose();
            }
        }
    }
}
