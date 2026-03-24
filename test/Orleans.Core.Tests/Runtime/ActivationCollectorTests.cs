using System.Collections.Concurrent;
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
    public class ActivationCollectorTests
    {
        private readonly FakeTimeProvider timeProvider;
        private readonly ActivationCollector collector;

        public ActivationCollectorTests()
        {
            var grainCollectionOptions = Options.Create(new GrainCollectionOptions());
            var logger = NullLogger<ActivationCollector>.Instance;

            this.timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00.000+00:00"));
            this.collector = new ActivationCollector(timeProvider, grainCollectionOptions, logger, new EnvironmentStatisticsProvider());
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

        [Theory, TestCategory("MemoryBasedDeactivations")]
        [InlineData(80.0, 70.0, 1000, 150, 100, true, 82)] // Over threshold, need to deactivate
        [InlineData(80.0, 70.0, 1000, 250, 100, false, 0)] // Below threshold, no deactivation
        [InlineData(80.0, 70.0, 1000, 100, 200, true, 155)] // More activations, smaller per-activation size
        [InlineData(80.0, 70.0, 1000, 800, 100, false, 0)] // Well below threshold
        [InlineData(80.0, 70.0, 1000, 50,  10,  true, 7)] // Few activations, large per-activation size
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
                statsProvider
            );

            collector._activationCount = activationCount;
            var overloaded = collector.IsMemoryOverloaded(out var surplusActivations);

            Assert.Equal(expectedOverloaded, overloaded);
            if (overloaded)
            {
                Assert.Equal(expectedActivationsTarget, activationCount - surplusActivations);
            }
        }

        [Fact]
        public async Task DeactivateInDueTimeOrder_OnlyOldestAndEligibleAreDeactivated()
        {
            var grainCollectionOptions = Options.Create(new GrainCollectionOptions());

            var logger = NullLogger<ActivationCollector>.Instance;
            var statsProvider = Substitute.For<IEnvironmentStatisticsProvider>();
            var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);

            var collector = new ActivationCollector(timeProvider, grainCollectionOptions, logger, statsProvider);
            var timer = Substitute.For<IAsyncTimer>();
            timer.NextTick().Returns(Task.FromResult(false));
            var timerFactory = Substitute.For<IAsyncTimerFactory>();
            timerFactory.Create(Arg.Any<TimeSpan>(), Arg.Any<string>()).Returns(timer);

            var wsLogger = NullLogger<ActivationWorkingSet>.Instance;
            var workingSet = new ActivationWorkingSet(timerFactory, wsLogger, new[] { collector });

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

            var collector = new ActivationCollector(timeProvider, grainCollectionOptions, logger, statsProvider);
            var timer = Substitute.For<IAsyncTimer>();
            timer.NextTick().Returns(Task.FromResult(false));
            var timerFactory = Substitute.For<IAsyncTimerFactory>();
            timerFactory.Create(Arg.Any<TimeSpan>(), Arg.Any<string>()).Returns(timer);

            var wsLogger = NullLogger<ActivationWorkingSet>.Instance;
            var workingSet = new ActivationWorkingSet(timerFactory, wsLogger, new[] { collector });

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
            var cts = new CancellationTokenSource();

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
            });

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
            });

            // Task 3: Run DeactivateInDueTimeOrder MANY times concurrently
            // This is where OrderBy enumerates buckets and can race with add/remove
            var deactivateTasks = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
            {
                for (int i = 0; i < 100; i++)
                {
                    try
                    {
                        // Deactivation iterates through the buckets, and if code is not resilient for concurrent modification,
                        // it will blow up with some form of collection modification exception.                        
                        await collector.DeactivateInDueTimeOrder(50, CancellationToken.None);
                        await Task.Delay(1);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            })).ToArray();

            // Wait for all deactivation attempts
            await Task.WhenAll(deactivateTasks);

            // Stop background modifications
            cts.Cancel();
            await Task.WhenAll(addTask, removeTask);

            // Verify no exceptions occurred during deactivation
            Assert.Empty(exceptions);
        }

        private IActivationWorkingSetMember PrepareActivation(int collectionAgeLimitMinutes, ActivationCollector collector)
            => PrepareActivation(TimeSpan.FromMinutes(collectionAgeLimitMinutes), collector);

        private IActivationWorkingSetMember PrepareActivation(TimeSpan collectionAgeLimit, ActivationCollector collector)
        {
            var activation = Substitute.For<ICollectibleGrainContext, IActivationWorkingSetMember>();
            activation.CollectionAgeLimit.Returns(collectionAgeLimit);
            activation.IsValid.Returns(true);
            activation.IsExemptFromCollection.Returns(false);
            activation.IsInactive.Returns(true);
            activation.Deactivated.Returns(Task.CompletedTask).AndDoes(_ => { Interlocked.Decrement(ref collector._activationCount); });

            return (IActivationWorkingSetMember)activation;
        }
    }
}
