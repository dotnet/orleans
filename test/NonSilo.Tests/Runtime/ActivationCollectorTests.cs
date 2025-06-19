using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
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
            long usedMemoryBytes = (maxMemoryMb - availableMemoryMb);
            long rawAvailableMemoryBytes = availableMemoryMb;
            long maxMemoryBytes = maxMemoryMb;

            var statsProvider = Substitute.For<IEnvironmentStatisticsProvider>();
            statsProvider.GetEnvironmentStatistics().Returns(
                new EnvironmentStatistics(
                    cpuUsagePercentage: 0,
                    rawCpuUsagePercentage: 0,
                    memoryUsageBytes: usedMemoryBytes, // used memory
                    rawMemoryUsageBytes: usedMemoryBytes, // used memory
                    availableMemoryBytes: rawAvailableMemoryBytes, // for compatibility
                    rawAvailableMemoryBytes: rawAvailableMemoryBytes, // for new logic
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
            var overloaded = collector.IsMemoryOverloaded(GC.CollectionCount(2), out var activationsTarget);

            Assert.Equal(expectedOverloaded, overloaded);
            Assert.Equal(expectedActivationsTarget, activationsTarget);
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

            var now = DateTime.UtcNow;
            var activation1 = Substitute.For<ICollectibleGrainContext, IActivationWorkingSetMember>();
            activation1.CollectionAgeLimit.Returns(TimeSpan.FromMinutes(1));
            activation1.IsValid.Returns(true);
            activation1.IsExemptFromCollection.Returns(false);
            activation1.IsInactive.Returns(true);
            activation1.Deactivated.Returns(Task.CompletedTask).AndDoes(_ => { Interlocked.Decrement(ref collector._activationCount); });

            var activation2 = Substitute.For<ICollectibleGrainContext, IActivationWorkingSetMember>();
            activation2.CollectionAgeLimit.Returns(TimeSpan.FromMinutes(1));
            activation2.IsValid.Returns(true);
            activation2.IsExemptFromCollection.Returns(false);
            activation2.IsInactive.Returns(true);
            activation2.Deactivated.Returns(Task.CompletedTask).AndDoes(_ => { Interlocked.Decrement(ref collector._activationCount); });

            var activation3 = Substitute.For<ICollectibleGrainContext, IActivationWorkingSetMember>();
            activation3.CollectionAgeLimit.Returns(TimeSpan.FromMinutes(1));
            activation3.IsValid.Returns(true);
            activation3.IsExemptFromCollection.Returns(false);
            activation3.IsInactive.Returns(true);
            activation3.Deactivated.Returns(Task.CompletedTask).AndDoes(_ => { Interlocked.Decrement(ref collector._activationCount); });

            ((IActivationWorkingSetMember)activation1).IsCandidateForRemoval(Arg.Any<bool>()).Returns(true);
            ((IActivationWorkingSetMember)activation2).IsCandidateForRemoval(Arg.Any<bool>()).Returns(true);
            ((IActivationWorkingSetMember)activation3).IsCandidateForRemoval(Arg.Any<bool>()).Returns(true);

            workingSet.OnActivated(activation1 as IActivationWorkingSetMember);
            workingSet.OnActivated(activation2 as IActivationWorkingSetMember);
            workingSet.OnActivated(activation3 as IActivationWorkingSetMember);

            await collector.DeactivateInDueTimeOrder(2, CancellationToken.None);

            Assert.Equal(1, collector._activationCount);
        }
    }
}
