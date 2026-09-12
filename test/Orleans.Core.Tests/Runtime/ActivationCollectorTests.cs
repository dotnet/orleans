using System.Collections.Concurrent;
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
using System.Diagnostics.Metrics;
using System.Threading.Channels;

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

        private IActivationWorkingSetMember PrepareActivation(int collectionAgeLimitMinutes, ActivationCollector collector)
            => PrepareActivation(TimeSpan.FromMinutes(collectionAgeLimitMinutes), collector);

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

        [Theory, TestCategory("Activation")]
        [InlineData("evicted")]
        [InlineData("deactivating")]
        [InlineData("deactivated")]
        public async Task WorkingSet_NotificationsBalancePerTypeCounts(string notification)
        {
            using var metrics = new AccountingMetricFixture();
            await using var scans = new WorkingSetScanDriver(timeProvider);
            var observer = Substitute.For<IActivationWorkingSetObserver>();
            var workingSet = new ActivationWorkingSet(scans.Factory, NullLogger<ActivationWorkingSet>.Instance, [observer], metrics.Instruments, timeProvider);
            var first = WorkingSetMember("working-orders", "one");
            var second = WorkingSetMember("working-orders", "two");
            var other = WorkingSetMember("working-invoices", "one");
            workingSet.OnActivated(first);
            workingSet.OnActivated(second);
            workingSet.OnActivated(other);
            Assert.Throws<InvalidOperationException>(() => workingSet.OnActivated(first));
            workingSet.OnActive(first);
            workingSet.OnActive(first);
            observer.Received(1).OnAdded(first);
            observer.Received(2).OnActive(first);

            metrics.StartListening(); // No recording was enabled while members were added.
            metrics.AssertWorkingSetSnapshot(workingSet, ("working-orders", 2), ("working-invoices", 1));
            metrics.AssertWorkingSetSnapshot(workingSet, ("working-orders", 2), ("working-invoices", 1));
            Assert.Equal(3, workingSet.Members.Count());
            switch (notification)
            {
                case "evicted": workingSet.OnEvicted(first); break;
                case "deactivating": workingSet.OnDeactivating(first); break;
                case "deactivated": workingSet.OnDeactivated(first); break;
                default: throw new ArgumentOutOfRangeException(nameof(notification));
            }
            // Check the selected removal before another notification can mask a missing eviction.
            observer.Received(1).OnEvicted(first);
            Assert.DoesNotContain(first, workingSet.Members);
            metrics.AssertWorkingSetSnapshot(workingSet, ("working-orders", 1), ("working-invoices", 1));
            workingSet.OnEvicted(first);
            workingSet.OnDeactivating(first);
            workingSet.OnDeactivated(first);
            observer.Received(1).OnEvicted(first);
            observer.Received(notification == "deactivating" ? 2 : 1).OnDeactivating(first);
            observer.Received(notification == "deactivated" ? 2 : 1).OnDeactivated(first);
            Assert.DoesNotContain(first, workingSet.Members);
            Assert.Contains(other, workingSet.Members);
            metrics.AssertWorkingSetSnapshot(workingSet, ("working-orders", 1), ("working-invoices", 1));

            // OnActive can also add a previously evicted member, without a second OnAdded notification.
            workingSet.OnActive(first);
            workingSet.OnActive(first);
            observer.Received(1).OnAdded(first);
            observer.Received(4).OnActive(first);
            Assert.Contains(first, workingSet.Members);
            metrics.AssertWorkingSetSnapshot(workingSet, ("working-orders", 2), ("working-invoices", 1));
            workingSet.OnDeactivated(first);
            workingSet.OnDeactivated(second);
            workingSet.OnDeactivated(other);
            workingSet.OnEvicted(first);
            observer.Received(2).OnEvicted(first);
            observer.Received(1).OnEvicted(second);
            observer.Received(1).OnEvicted(other);
            Assert.Empty(workingSet.Members);
            metrics.AssertWorkingSetSnapshot(workingSet, ("working-orders", 0), ("working-invoices", 0));
            metrics.AssertWorkingSetSnapshot(workingSet, ("working-orders", 0), ("working-invoices", 0));
            Assert.Empty(metrics.Events);
        }

        [Fact, TestCategory("Activation")]
        public async Task WorkingSet_NonContextMemberUsesUnknown()
        {
            using var metrics = new AccountingMetricFixture();
            await using var scans = new WorkingSetScanDriver(timeProvider);
            var observer = Substitute.For<IActivationWorkingSetObserver>();
            var workingSet = new ActivationWorkingSet(scans.Factory, NullLogger<ActivationWorkingSet>.Instance, [observer], metrics.Instruments, timeProvider);
            var typed = WorkingSetMember("working-typed", "one");
            var unknown = Substitute.For<IActivationWorkingSetMember>();
            Assert.False(unknown is IGrainContext);
            workingSet.OnActivated(typed);
            workingSet.OnActivated(unknown);
            workingSet.OnActive(unknown);
            metrics.StartListening();
            metrics.AssertWorkingSetSnapshot(workingSet, ("working-typed", 1), ("unknown", 1));
            metrics.AssertWorkingSetSnapshot(workingSet, ("working-typed", 1), ("unknown", 1));

            workingSet.OnDeactivating(unknown);
            workingSet.OnDeactivated(unknown);
            workingSet.OnEvicted(unknown);
            Assert.Same(typed, Assert.Single(workingSet.Members));
            observer.Received(1).OnEvicted(unknown);
            observer.DidNotReceive().OnEvicted(typed);
            metrics.AssertWorkingSetSnapshot(workingSet, ("working-typed", 1), ("unknown", 0));
            workingSet.OnDeactivated(typed);
            Assert.Empty(workingSet.Members);
            metrics.AssertWorkingSetSnapshot(workingSet, ("working-typed", 0), ("unknown", 0));
            Assert.Empty(metrics.Events);
        }

        [Fact, TestCategory("Activation")]
        public Task WorkingSet_TwoIdleScansRetainThenEvict() => AssertIdleScanAccounting(revive: false);

        [Fact, TestCategory("Activation")]
        public Task WorkingSet_ActivityBetweenIdleScansRevives() => AssertIdleScanAccounting(revive: true);

        private async Task AssertIdleScanAccounting(bool revive)
        {
            using var metrics = new AccountingMetricFixture();
            await using var scans = new WorkingSetScanDriver(timeProvider);
            var observer = Substitute.For<IActivationWorkingSetObserver>();
            var workingSet = new ActivationWorkingSet(scans.Factory, NullLogger<ActivationWorkingSet>.Instance, [observer], metrics.Instruments, timeProvider);
            var candidate = WorkingSetMember("scan-orders", "one");
            var survivor = WorkingSetMember("scan-invoices", "one");
            candidate.IsCandidateForRemoval(Arg.Any<bool>()).Returns(true);
            survivor.IsCandidateForRemoval(Arg.Any<bool>()).Returns(false);
            workingSet.OnActivated(candidate);
            workingSet.OnActivated(survivor);
            metrics.StartListening();
            metrics.AssertWorkingSetSnapshot(workingSet, ("scan-orders", 1), ("scan-invoices", 1));
            await scans.StartAsync(workingSet);

            var firstIdle = ArmWorkingSetNotification(observer, candidate, idle: true);
            await scans.ScanAsync("first idle scan", firstIdle);
            Assert.Same(survivor, Assert.Single(workingSet.Members));
            observer.Received(1).OnIdle(candidate);
            observer.DidNotReceive().OnEvicted(candidate);
            candidate.Received(1).IsCandidateForRemoval(false);
            candidate.DidNotReceive().IsCandidateForRemoval(true);
            // Idle entries are invisible in Members but still included in Count and the gauge.
            metrics.AssertWorkingSetSnapshot(workingSet, ("scan-orders", 1), ("scan-invoices", 1));
            metrics.AssertWorkingSetSnapshot(workingSet, ("scan-orders", 1), ("scan-invoices", 1));

            if (revive)
            {
                workingSet.OnActive(candidate);
                workingSet.OnActive(candidate);
                Assert.Contains(candidate, workingSet.Members);
                Assert.Equal(2, workingSet.Members.Count());
                metrics.AssertWorkingSetSnapshot(workingSet, ("scan-orders", 1), ("scan-invoices", 1));
                candidate.IsCandidateForRemoval(false).Returns(false);
                var active = ArmWorkingSetNotification(observer, candidate, idle: false);
                await scans.ScanAsync("active scan after revival", active);
                Assert.Contains(candidate, workingSet.Members);
                observer.Received(1).OnIdle(candidate);
                observer.DidNotReceive().OnEvicted(candidate);
                observer.Received(3).OnActive(candidate);
                candidate.DidNotReceive().IsCandidateForRemoval(true);
                metrics.AssertWorkingSetSnapshot(workingSet, ("scan-orders", 1), ("scan-invoices", 1));

                candidate.IsCandidateForRemoval(false).Returns(true);
                var idleAgain = ArmWorkingSetNotification(observer, candidate, idle: true);
                await scans.ScanAsync("fresh idle scan after revival", idleAgain);
                Assert.Same(survivor, Assert.Single(workingSet.Members));
                observer.Received(2).OnIdle(candidate);
                observer.DidNotReceive().OnEvicted(candidate);
                metrics.AssertWorkingSetSnapshot(workingSet, ("scan-orders", 1), ("scan-invoices", 1));
            }

            var evicted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            observer.When(item => item.OnEvicted(candidate)).Do(_ => evicted.TrySetResult());
            await scans.ScanAsync("second eligible idle scan evicts", evicted.Task);
            observer.Received(1).OnEvicted(candidate);
            observer.Received(revive ? 2 : 1).OnIdle(candidate);
            candidate.Received(revive ? 3 : 1).IsCandidateForRemoval(false);
            candidate.Received(1).IsCandidateForRemoval(true);
            Assert.Same(survivor, Assert.Single(workingSet.Members));
            metrics.AssertWorkingSetSnapshot(workingSet, ("scan-orders", 0), ("scan-invoices", 1));

            var survivorActive = ArmWorkingSetNotification(observer, survivor, idle: false);
            await scans.ScanAsync("scan after eviction must not decrement again", survivorActive);
            workingSet.OnEvicted(candidate);
            workingSet.OnDeactivating(candidate);
            workingSet.OnDeactivated(candidate);
            observer.Received(1).OnEvicted(candidate);
            candidate.Received(revive ? 3 : 1).IsCandidateForRemoval(false);
            candidate.Received(1).IsCandidateForRemoval(true);
            survivor.Received(revive ? 5 : 3).IsCandidateForRemoval(false);
            survivor.DidNotReceive().IsCandidateForRemoval(true);
            observer.Received(revive ? 5 : 3).OnActive(survivor);
            metrics.AssertWorkingSetSnapshot(workingSet, ("scan-orders", 0), ("scan-invoices", 1));
            workingSet.OnDeactivated(survivor);
            Assert.Empty(workingSet.Members);
            metrics.AssertWorkingSetSnapshot(workingSet, ("scan-orders", 0), ("scan-invoices", 0));
            metrics.AssertWorkingSetSnapshot(workingSet, ("scan-orders", 0), ("scan-invoices", 0));
            Assert.Empty(metrics.Events);
        }

        [Fact, TestCategory("Activation")]
        public async Task Collector_NonemptyBatchHasOneUnknownShutdown()
        {
            using var metrics = new AccountingMetricFixture();
            using var batchCollector = new ActivationCollector(timeProvider, Options.Create(new GrainCollectionOptions()),
                NullLogger<ActivationCollector>.Instance, Substitute.For<IEnvironmentStatisticsProvider>(), metrics.Instruments);
            await using var scans = new WorkingSetScanDriver(timeProvider);
            var workingSet = new ActivationWorkingSet(scans.Factory, NullLogger<ActivationWorkingSet>.Instance, [batchCollector], metrics.Instruments, timeProvider);
            var ageLimit = TimeSpan.FromMinutes(1);
            var awaited = Enumerable.Range(0, 2).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var releases = Enumerable.Range(0, 2).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var completionReads = new int[2];
            var activations = new[] { "batch-orders", "batch-invoices" }.Select((type, index) =>
            {
                var activation = Substitute.For<ICollectibleGrainContext, IActivationWorkingSetMember>();
                ConfigureCollectionRegistrationSlot(activation);
                activation.GrainId.Returns(GrainId.Create(type, "one"));
                activation.CollectionAgeLimit.Returns(ageLimit);
                activation.IsExemptFromCollection.Returns(false);
                activation.TryDeactivateForCollection(Arg.Any<DeactivationReason>(), Arg.Any<DateTime>(),
                    Arg.Any<TimeSpan>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(ActivationCollectionResult.StartedDeactivation);
                activation.Deactivated.Returns(_ =>
                {
                    Interlocked.Increment(ref completionReads[index]);
                    awaited[index].TrySetResult();
                    return releases[index].Task;
                });
                return activation;
            }).ToArray();
            foreach (var activation in activations) workingSet.OnActivated((IActivationWorkingSetMember)activation);
            var unrelated = WorkingSetMember("batch-unrelated", "one");
            workingSet.OnActivated(unrelated);
            metrics.StartListening();
            Assert.Empty(metrics.Events);
            Assert.Equal(2, batchCollector._activationCount);
            metrics.AssertWorkingSetSnapshot(workingSet, ("batch-orders", 1), ("batch-invoices", 1), ("batch-unrelated", 1));
            var completionObservers = Task.WhenAll(awaited.Select(signal => signal.Task).ToArray());
            timeProvider.Advance(ageLimit);
            var cancellationToken = TestContext.Current.CancellationToken;
            var collection = batchCollector.CollectStaleActivations(cancellationToken);
            try
            {
                await completionObservers.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                Assert.False(collection.IsCompleted);
                Assert.Equal(new[] { 1, 1 }, completionReads);
                AssertCollectionEvents(metrics.Events, passes: 1, batches: 1);
                foreach (var activation in activations)
                {
                    Assert.Equal(default, batchCollector.GetCollectionTicketForTesting(activation));
                    activation.Received(1).TryDeactivateForCollection(
                        Arg.Is<DeactivationReason>(reason => reason.ReasonCode == DeactivationReasonCode.ActivationIdle),
                        timeProvider.GetUtcNow().UtcDateTime, ageLimit, true, cancellationToken);
                    workingSet.OnDeactivating((IActivationWorkingSetMember)activation);
                    workingSet.OnDeactivated((IActivationWorkingSetMember)activation);
                }
                releases[0].SetResult();
                Assert.False(collection.IsCompleted); // The other activation still owns an incomplete completion.
                releases[1].SetResult();
                await collection.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

                Assert.Equal(0, batchCollector._activationCount);
                Assert.Equal(new[] { 1, 1 }, completionReads);
                Assert.All(activations, activation => Assert.False(batchCollector.HasActiveCollectionRegistrationForTesting(activation)));
                Assert.Same(unrelated, Assert.Single(workingSet.Members));
                metrics.AssertWorkingSetSnapshot(workingSet, ("batch-orders", 0), ("batch-invoices", 0), ("batch-unrelated", 1));
                metrics.AssertWorkingSetSnapshot(workingSet, ("batch-orders", 0), ("batch-invoices", 0), ("batch-unrelated", 1));
                // Historical collector scope: one unknown shutdown for the batch, not one per fake context.
                AssertCollectionEvents(metrics.Events, passes: 1, batches: 1);
                await batchCollector.CollectStaleActivations(cancellationToken);
                AssertCollectionEvents(metrics.Events, passes: 2, batches: 1);
                Assert.Equal(new[] { 1, 1 }, completionReads);
                metrics.AssertWorkingSetSnapshot(workingSet, ("batch-orders", 0), ("batch-invoices", 0), ("batch-unrelated", 1));
            }
            finally
            {
                foreach (var release in releases) release.TrySetResult();
                await collection.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            }
        }

        [Fact, TestCategory("Activation")]
        public async Task Collector_EmptyPassHasNoBatchShutdown()
        {
            using var metrics = new AccountingMetricFixture();
            using var emptyCollector = new ActivationCollector(timeProvider, Options.Create(new GrainCollectionOptions()),
                NullLogger<ActivationCollector>.Instance, Substitute.For<IEnvironmentStatisticsProvider>(), metrics.Instruments);
            await using var scans = new WorkingSetScanDriver(timeProvider);
            var workingSet = new ActivationWorkingSet(scans.Factory, NullLogger<ActivationWorkingSet>.Instance, [emptyCollector], metrics.Instruments, timeProvider);
            var unrelated = WorkingSetMember("empty-pass-unrelated", "one");
            workingSet.OnActivated(unrelated);
            metrics.StartListening();
            Assert.Empty(metrics.Events);
            metrics.AssertWorkingSetSnapshot(workingSet, ("empty-pass-unrelated", 1));

            await emptyCollector.CollectStaleActivations(TestContext.Current.CancellationToken);

            AssertCollectionEvents(metrics.Events, passes: 1, batches: 0);
            Assert.Equal(0, emptyCollector._activationCount);
            Assert.Same(unrelated, Assert.Single(workingSet.Members));
            metrics.AssertWorkingSetSnapshot(workingSet, ("empty-pass-unrelated", 1));
            metrics.AssertWorkingSetSnapshot(workingSet, ("empty-pass-unrelated", 1));
            Assert.False(metrics.Instruments.TryGetGrainTypeMetrics(default, out _));
        }

        private static IActivationWorkingSetMember WorkingSetMember(string type, string key)
        {
            var context = Substitute.For<IGrainContext, IActivationWorkingSetMember>();
            context.GrainId.Returns(GrainId.Create(type, key));
            return (IActivationWorkingSetMember)context;
        }

        private static Task ArmWorkingSetNotification(IActivationWorkingSetObserver observer, IActivationWorkingSetMember member, bool idle)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (idle) observer.When(item => item.OnIdle(member)).Do(_ => signal.TrySetResult());
            else observer.When(item => item.OnActive(member)).Do(_ => signal.TrySetResult());
            return signal.Task;
        }

        private static void AssertCollectionEvents(AccountingMeasurement[] events, int passes, int batches)
        {
            Assert.Equal(passes + batches, events.Length); // No unrelated lifecycle counters may change.
            var collections = events.Where(item => item.Instrument.Name == InstrumentNames.CATALOG_ACTIVATION_COLLECTION_NUMBER_OF_COLLECTIONS).ToArray();
            Assert.Equal(passes, collections.Length);
            Assert.All(collections, item =>
            {
                Assert.IsType<Counter<int>>(item.Instrument);
                Assert.Equal(1, item.Value);
                Assert.Empty(item.Tags);
            });
            var shutdowns = events.Where(item => item.Instrument.Name == InstrumentNames.CATALOG_ACTIVATION_SHUTDOWN).ToArray();
            Assert.Equal(batches, shutdowns.Length);
            Assert.All(shutdowns, item =>
            {
                Assert.IsType<Counter<int>>(item.Instrument);
                Assert.Equal(1, item.Value);
                Assert.Equal(2, item.Tags.Length);
                Assert.Equal("collection", Assert.Single(item.Tags, tag => tag.Key == "via").Value);
                Assert.Same(GrainTypeMetrics.UnknownGrainType, Assert.Single(item.Tags, tag => tag.Key == "grain_type").Value);
            });
        }

        private sealed record AccountingMeasurement(Instrument Instrument, int Value, KeyValuePair<string, object?>[] Tags);

        private sealed class AccountingMetricFixture : IDisposable
        {
            private readonly ServiceProvider _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            private readonly Meter _meter;
            private readonly ConcurrentQueue<AccountingMeasurement> _events = new();
            private readonly List<AccountingMeasurement> _snapshot = [];
            private MeterListener? _listener;
            public CatalogInstruments Instruments { get; }
            public AccountingMeasurement[] Events => _events.ToArray();

            public AccountingMetricFixture()
            {
                var instruments = new OrleansInstruments(_provider.GetRequiredService<IMeterFactory>());
                _meter = instruments.Meter;
                Instruments = new CatalogInstruments(instruments);
                // The working set owns its gauge registration; this fixture must never register it.
            }

            public void StartListening()
            {
                Assert.Null(_listener);
                _listener = new MeterListener();
                _listener.InstrumentPublished = (instrument, listener) =>
                {
                    if (ReferenceEquals(instrument.Meter, _meter)) listener.EnableMeasurementEvents(instrument);
                };
                _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
                {
                    var measurement = new AccountingMeasurement(instrument, value, tags.ToArray());
                    if (instrument is ObservableGauge<int>) _snapshot.Add(measurement);
                    else _events.Enqueue(measurement);
                });
                _listener.Start();
            }

            public void AssertWorkingSetSnapshot(ActivationWorkingSet workingSet, params (string Type, int Count)[] expected)
            {
                Assert.NotNull(_listener);
                _snapshot.Clear();
                _listener.RecordObservableInstruments();
                Assert.Equal(expected.Length, _snapshot.Count);
                foreach (var (type, count) in expected)
                {
                    var item = Assert.Single(_snapshot, item => Equals(Assert.Single(item.Tags).Value, type));
                    Assert.IsType<ObservableGauge<int>>(item.Instrument);
                    Assert.Equal(InstrumentNames.CATALOG_ACTIVATION_WORKING_SET, item.Instrument.Name);
                    Assert.Equal(count, item.Value);
                    var tag = Assert.Single(item.Tags);
                    Assert.Equal("grain_type", tag.Key);
                    Assert.True(Instruments.TryGetGrainTypeMetrics(type == "unknown" ? default : GrainType.Create(type), out var cached));
                    Assert.Same(cached.GrainTypeTagValue, tag.Value);
                }
                Assert.Equal(expected.Sum(item => item.Count), workingSet.Count);
                Assert.Equal(workingSet.Count, _snapshot.Sum(item => item.Value));
            }

            public void Dispose()
            {
                _listener?.Dispose();
                _provider.Dispose();
            }
        }

        private sealed class WorkingSetScanDriver : IAsyncDisposable
        {
            private static readonly TimeSpan Period = TimeSpan.FromSeconds(5);
            private readonly FakeTimeProvider _clock;
            private readonly CancellationTokenSource _stop = new();
            private readonly Channel<int> _scheduled = Channel.CreateUnbounded<int>();
            private readonly IAsyncTimer _timer = Substitute.For<IAsyncTimer>();
            private ILifecycleObserver? _lifecycleObserver;
            private int _scheduleNumber;
            private int _observedSchedule;
            public IAsyncTimerFactory Factory { get; } = Substitute.For<IAsyncTimerFactory>();

            public WorkingSetScanDriver(FakeTimeProvider clock)
            {
                _clock = clock;
                _timer.NextTick().Returns(_ => NextTick());
                _timer.When(timer => timer.Dispose()).Do(_ => _stop.Cancel());
                Factory.Create(Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<TimeProvider>()).Returns(call =>
                {
                    Assert.Equal(Period, call.Arg<TimeSpan>());
                    Assert.Equal("ActivationWorkingSet.MonitorWorkingSet", call.Arg<string>());
                    Assert.Same(_clock, call.Arg<TimeProvider>());
                    return _timer;
                });
            }

            public async Task StartAsync(ActivationWorkingSet workingSet)
            {
                var lifecycle = Substitute.For<ISiloLifecycle>();
                lifecycle.Subscribe(nameof(ActivationWorkingSet), ServiceLifecycleStage.BecomeActive, Arg.Any<ILifecycleObserver>())
                    .Returns(call =>
                    {
                        _lifecycleObserver = call.Arg<ILifecycleObserver>();
                        return Substitute.For<IDisposable>();
                    });
                ((ILifecycleParticipant<ISiloLifecycle>)workingSet).Participate(lifecycle);
                lifecycle.Received(1).Subscribe(nameof(ActivationWorkingSet), ServiceLifecycleStage.BecomeActive, Arg.Any<ILifecycleObserver>());
                Assert.NotNull(_lifecycleObserver);
                await _lifecycleObserver.OnStart(TestContext.Current.CancellationToken);
                await AwaitSchedule("monitor startup");
            }

            public async Task ScanAsync(string phase, Task notification)
            {
                Assert.False(notification.IsCompleted, $"Observer must be armed before {phase}.");
                _clock.Advance(Period); // The sole clock driver, with a scheduled fake delay already observed.
                try
                {
                    await notification.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                    await AwaitSchedule(phase);
                }
                catch (TimeoutException exception)
                {
                    throw new TimeoutException($"Working-set {phase}: expected notification and next schedule after {_observedSchedule}.", exception);
                }
            }

            private async Task AwaitSchedule(string phase)
            {
                var schedule = await _scheduled.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                Assert.True(schedule == ++_observedSchedule, $"{phase}: expected schedule {_observedSchedule}, observed {schedule}.");
            }

            private async Task<bool> NextTick()
            {
                var delay = Task.Delay(Period, _clock, _stop.Token);
                _scheduled.Writer.TryWrite(++_scheduleNumber); // Delay is installed before publishing readiness.
                try
                {
                    await delay;
                    return true;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    return false;
                }
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    if (_lifecycleObserver is not null)
                    {
                        await _lifecycleObserver.OnStop(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                        _timer.Received(1).Dispose();
                    }
                }
                finally
                {
                    _stop.Cancel();
                    _stop.Dispose();
                }
            }
        }
    }
}
