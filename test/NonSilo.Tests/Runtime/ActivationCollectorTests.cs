using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Statistics;
using Xunit;

namespace UnitTests.Runtime
{
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
        [InlineData(80.0, 70.0, 1000, 150, 100, true, 18)] // Over threshold, need to deactivate
        [InlineData(80.0, 70.0, 1000, 250, 100, false, 0)] // Below threshold, no deactivation
        [InlineData(80.0, 70.0, 1000, 100, 200, true, 45)]  // More activations, smaller per-activation size
        [InlineData(80.0, 70.0, 1000, 800, 100, false, 0)] // Well below threshold
        [InlineData(80.0, 70.0, 1000, 50, 10, true, 3)]    // Few activations, large per-activation size
        public void IsMemoryOverloaded_WorksAsExpected(
            double memoryLoadThreshold,
            double targetMemoryLoad,
            long maxMemoryMb,
            long availableMemoryMb,
            int activationCount,
            bool expectedOverloaded,
            int expectedToDeactivate)
        {
            var options = new GrainCollectionOptions
            {
                MemoryBasedOptions = new MemoryBasedGrainCollectionOptions
                {
                    MemoryLoadThresholdPercentage = memoryLoadThreshold,
                    TargetMemoryLoadPercentage = targetMemoryLoad
                }
            };

            var statsProvider = Substitute.For<IEnvironmentStatisticsProvider>();
            statsProvider.GetEnvironmentStatistics().Returns(
                new EnvironmentStatistics(
                    cpuUsagePercentage: 0,
                    memoryUsageBytes: 0,
                    availableMemoryBytes: availableMemoryMb * 1024 * 1024,
                    maximumAvailableMemoryBytes: maxMemoryMb * 1024 * 1024
                )
            );

            var logger = NullLogger<ActivationCollector>.Instance;
            var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);

            var collector = new ActivationCollector(
                timeProvider,
                Options.Create(options),
                logger,
                statsProvider
            );

            collector._activationCount = activationCount;
            var overloaded = collector.IsMemoryOverloaded(out var toDeactivate);

            Assert.Equal(expectedOverloaded, overloaded);
            Assert.Equal(expectedToDeactivate, toDeactivate);
        }
    }
}
