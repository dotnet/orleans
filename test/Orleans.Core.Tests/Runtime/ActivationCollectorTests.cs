using System.Collections.Concurrent;
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
            await lifecycle.OnStart();
            await scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var mutationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var mutationTask = Task.Run(() =>
            {
                mutationStarted.SetResult();
                workingSet.OnEvicted(member);
                workingSet.OnActivated(member);
            });
            await mutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                var stopTask = lifecycle.OnStop();
                Assert.False(stopTask.IsCompleted);
                Assert.False(mutationTask.IsCompleted);
                resumeScan.Set();
                await mutationTask;
                await stopTask;
            }
            finally
            {
                resumeScan.Set();
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
            await lifecycle.OnStart();
            await removalScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var mutationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var mutationTask = Task.Run(() =>
            {
                mutationStarted.SetResult();
                workingSet.OnEvicted(member);
                workingSet.OnActivated(member);
            });
            await mutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                var stopTask = lifecycle.OnStop();
                Assert.False(stopTask.IsCompleted);
                Assert.False(mutationTask.IsCompleted);
                resumeScan.Set();
                await mutationTask;
                await stopTask;
            }
            finally
            {
                resumeScan.Set();
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
            await lifecycle.OnStart();
            await lifecycle.OnStop();

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
            });
            await firstEviction.Task.WaitAsync(TimeSpan.FromSeconds(10));

            try
            {
                while (enumerator.MoveNext())
                {
                    enumeratedMembers.Add(enumerator.Current);
                }
            }
            finally
            {
                resumeWriter.Set();
            }

            await writer.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.All(enumeratedMembers, member => Assert.Contains(member, members));
            Assert.Equal(members.Length, workingSet.Count);
            Assert.True(members.Cast<IActivationWorkingSetMember>().ToHashSet().SetEquals(workingSet.Members));
            observer.Received(members.Length * 2).OnAdded(Arg.Any<IActivationWorkingSetMember>());
            observer.Received(members.Length).OnEvicted(Arg.Any<IActivationWorkingSetMember>());
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
            await lifecycle.OnStart();
            await removalScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var reactivationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reactivationTask = Task.Run(() =>
            {
                reactivationStarted.SetResult();
                workingSet.OnActive(member);
            });
            await reactivationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                Assert.False(reactivationTask.IsCompleted);
                resumeScan.Set();
                await reactivationTask;
                await lifecycle.OnStop();
            }
            finally
            {
                resumeScan.Set();
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
        public void ActivationStatus_PreservesLifecycleStateAcrossWorkingSetTransitions()
        {
            var activation = (ActivationData)RuntimeHelpers.GetUninitializedObject(typeof(ActivationData));
            var workingSetState = (IActivationWorkingSetMember)activation;

            lock (activation)
            {
                activation.SetState(ActivationState.Valid);
                workingSetState.IsInWorkingSet = true;
                workingSetState.IsIdle = true;
                Assert.Equal(ActivationState.Valid, activation.State);
                Assert.True(workingSetState.IsInWorkingSet);
                Assert.True(workingSetState.IsIdle);

                activation.SetState(ActivationState.Deactivating);
                workingSetState.IsInWorkingSet = false;
                workingSetState.IsIdle = false;
                Assert.Equal(ActivationState.Deactivating, activation.State);
                Assert.False(workingSetState.IsInWorkingSet);
                Assert.False(workingSetState.IsIdle);
            }
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

        private sealed class TestWorkingSetMember(Func<bool, bool>? isCandidateForRemoval = null) : IActivationWorkingSetMember
        {
            private bool _isIdle;
            private bool _isInWorkingSet;

            public bool IsIdle
            {
                get => Volatile.Read(ref _isIdle);
                set
                {
                    Assert.True(Monitor.IsEntered(this));
                    _isIdle = value;
                }
            }

            public bool IsInWorkingSet
            {
                get => Volatile.Read(ref _isInWorkingSet);
                set
                {
                    Assert.True(Monitor.IsEntered(this));
                    _isInWorkingSet = value;
                }
            }

            public bool IsCandidateForRemoval(bool wouldRemove)
            {
                Assert.True(Monitor.IsEntered(this));
                return isCandidateForRemoval?.Invoke(wouldRemove) ?? false;
            }

        }
    }
}
