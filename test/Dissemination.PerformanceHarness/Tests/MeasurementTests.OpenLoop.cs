using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Orleans.Dissemination.PerformanceHarness;

public sealed partial class MeasurementTests
{
    [Theory]
    [InlineData("OpenLoopSynchronized", 0)]
    [InlineData("OpenLoopStaggered", 250)]
    public async Task OpenLoopKeepsOneHertzDeadlinesIndependentOfPublicationDuration(string workload, int phaseMilliseconds)
    {
        var clock = new RecordingClock();
        var epoch = clock.GetUtcNow().AddSeconds(1);
        var producer = new OpenLoopProducer(clock);
        await using var cleanup = producer;
        producer.Start(new(epoch, 3, 1, 4, workload), async token =>
        {
            var start = clock.GetUtcNow();
            await Task.Delay(TimeSpan.FromMilliseconds(200), clock, token);
            return new(start, clock.GetUtcNow(), start.UtcTicks, start.ToString("O"));
        }, TestContext.Current.CancellationToken);

        clock.Advance(await clock.NextDelay());
        for (var index = 0; index < 3; index++)
        {
            var publicationDelay = await clock.NextDelay();
            Assert.Equal(TimeSpan.FromMilliseconds(200), publicationDelay);
            clock.Advance(publicationDelay);
            var nextDeadline = await clock.NextDelay();
            Assert.Equal(TimeSpan.FromMilliseconds(index < 2 ? 800 : 800 - phaseMilliseconds), nextDeadline);
            clock.Advance(nextDeadline);
        }
        var result = await producer.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Publications.Length);
        Assert.Equal(0, result.MissedPeriods);
        Assert.Equal(0, result.Overruns);
        for (var index = 0; index < 3; index++)
        {
            var sample = result.Publications[index];
            Assert.Equal(epoch.AddSeconds(index).AddMilliseconds(phaseMilliseconds), sample.ScheduledAtUtc);
            Assert.Equal(sample.ScheduledAtUtc, sample.Result.StartedAtUtc);
            Assert.Equal(TimeSpan.FromMilliseconds(200), sample.Result.CompletedAtUtc - sample.Result.StartedAtUtc);
        }
        Assert.Equal(new OpenLoopProgress(3, true, 0, 0, null), producer.Progress);
    }

    [Fact]
    public void StaggeredScheduleEvenlyDistributesEveryProducerAcrossOneSecond()
    {
        for (var count = 3; count <= 32; count++)
        {
            var offsets = Enumerable.Range(0, count)
                .Select(index => new OpenLoopPlan(default, 3, index, count, "OpenLoopStaggered").Offset(0)).ToArray();
            for (var index = 0; index < count; index++)
            {
                Assert.Equal(TimeSpan.TicksPerSecond * index / count, offsets[index].Ticks);
                Assert.Equal(TimeSpan.Zero, new OpenLoopPlan(default, 3, index, count, "OpenLoopSynchronized").Offset(0));
            }
        }
    }

    [Theory]
    [InlineData(2, 4, 0, "OpenLoopSynchronized")]
    [InlineData(31, 4, 0, "OpenLoopSynchronized")]
    [InlineData(3, 2, 0, "OpenLoopSynchronized")]
    [InlineData(3, 33, 0, "OpenLoopSynchronized")]
    [InlineData(3, 4, -1, "OpenLoopSynchronized")]
    [InlineData(3, 4, 4, "OpenLoopSynchronized")]
    [InlineData(3, 4, 0, "ClosedLoop")]
    public void OpenLoopRejectsUnboundedOrInvalidPlans(int seconds, int count, int index, string workload) =>
        Assert.Throws<InvalidOperationException>(() => new OpenLoopPlan(default, seconds, index, count, workload).Validate());

    [Fact]
    public async Task OpenLoopReportsMissedPeriodsWithoutCatchUp()
    {
        var clock = new RecordingClock();
        var producer = new OpenLoopProducer(clock);
        producer.Start(new(clock.GetUtcNow().AddSeconds(1), 3, 0, 3, "OpenLoopSynchronized"),
            _ => throw new InvalidOperationException("A missed slot must not publish."), TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromSeconds(1), await clock.NextDelay());
        clock.Advance(TimeSpan.FromSeconds(2));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => producer.Completion);
        Assert.Contains("missed publication slot 0", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, producer.Progress!.MissedPeriods);
        Assert.Equal(0, producer.Progress.PublishedCount);
        Assert.Empty(producer.Report.Publications);
        await Assert.ThrowsAsync<InvalidOperationException>(() => producer.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task OpenLoopReportsOverrunAndPreservesTheCompletedPublication()
    {
        var clock = new RecordingClock();
        var producer = new OpenLoopProducer(clock);
        producer.Start(new(clock.GetUtcNow().AddSeconds(1), 3, 0, 3, "OpenLoopSynchronized"), async token =>
        {
            var start = clock.GetUtcNow();
            await Task.Delay(TimeSpan.FromMilliseconds(1500), clock, token);
            return new(start, clock.GetUtcNow(), start.UtcTicks, "overrun");
        }, TestContext.Current.CancellationToken);
        clock.Advance(await clock.NextDelay());
        clock.Advance(await clock.NextDelay());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => producer.Completion);
        Assert.Contains("exceeded its 1 Hz slot", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, producer.Progress!.Overruns);
        var sample = Assert.Single(producer.Report.Publications);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), sample.Result.CompletedAtUtc - sample.Result.StartedAtUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => producer.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task OpenLoopCancellationStopsArmedPublication()
    {
        var clock = new RecordingClock();
        var producer = new OpenLoopProducer(clock);
        using var cancellation = new CancellationTokenSource();
        producer.Start(new(clock.GetUtcNow().AddSeconds(1), 3, 0, 3, "OpenLoopSynchronized"),
            _ => throw new InvalidOperationException("Canceled work must not publish."), cancellation.Token);
        await clock.NextDelay();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => producer.Completion);
        Assert.Equal(0, producer.Progress!.PublishedCount);
        await producer.DisposeAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    public void OpenLoopRejectsElapsedOrUnboundedStartEpoch(int leadSeconds)
    {
        var clock = new RecordingClock();
        var producer = new OpenLoopProducer(clock);
        Assert.Throws<InvalidOperationException>(() =>
            producer.Start(new(clock.GetUtcNow().AddSeconds(leadSeconds), 3, 0, 3, "OpenLoopSynchronized"),
                _ => throw new InvalidOperationException("Invalid plans must not publish."), TestContext.Current.CancellationToken));
        Assert.Null(producer.Progress);
    }

    [Fact]
    public async Task OpenLoopArmingIsSingleUse()
    {
        var clock = new RecordingClock();
        var producer = new OpenLoopProducer(clock);
        var plan = new OpenLoopPlan(clock.GetUtcNow().AddSeconds(1), 3, 0, 3, "OpenLoopSynchronized");
        producer.Start(plan, _ => throw new InvalidOperationException("The producer is canceled before its start."), TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => producer.Start(plan, _ => throw new InvalidOperationException(), TestContext.Current.CancellationToken));
        await producer.DisposeAsync();
        Assert.Equal(0, producer.Progress!.PublishedCount);
    }

    [Fact]
    public void OpenLoopReportsPollingUpperBoundsAndUnobservedIntermediateValues()
    {
        var reports = CreateOpenLoopReports();
        var measurements = CreateOpenLoopMeasurements();
        ObserveOpenLoop(measurements, reports, sequence: 2);
        var result = measurements.Complete(reports);

        Assert.Equal(1, result.ProducerRateHz);
        Assert.Equal(9, result.ObservedPublicationPairs);
        Assert.Equal(18, result.UnobservedPublicationPairs);
        Assert.Equal(6, result.FirstObservedAgeUpperBoundMilliseconds.Length);
        Assert.Equal(new double[] { 102, 102, 102 }, result.FinalLatestLatencyUpperBoundMilliseconds);
        Assert.Equal(6, result.ActualPublicationPeriodsMilliseconds.Length);
        Assert.All(result.ActualPublicationPeriodsMilliseconds, value => Assert.Equal(1000, value));
        Assert.All(result.PublicationMilliseconds, value => Assert.Equal(10, value));
        Assert.Equal(0, result.MaximumStartLatenessMilliseconds);
    }

    [Theory]
    [InlineData("value")]
    [InlineData("version")]
    [InlineData("latest")]
    [InlineData("process")]
    [InlineData("overrun")]
    public void OpenLoopRejectsMismatchedStateMissingLatestOrInvalidProducer(string mismatch)
    {
        var reports = CreateOpenLoopReports();
        var measurements = CreateOpenLoopMeasurements();
        ObserveOpenLoop(measurements, reports, mismatch == "latest" ? 1 : 2, mismatch);
        if (mismatch == "process")
        {
            reports[0] = reports[0] with { ProcessId = 999 };
        }
        else if (mismatch == "overrun")
        {
            reports[0] = reports[0] with { Report = reports[0].Report with { Overruns = 1 } };
        }
        Assert.Throws<InvalidOperationException>(() => measurements.Complete(reports));
    }

    [Fact]
    public void ScalingComparisonSeparatesOpenLoopLatencyAndRejectsMixedWorkloads()
    {
        var measurements = CreateOpenLoopMeasurements();
        ObserveOpenLoop(measurements, CreateOpenLoopReports(), 2);
        var summary = measurements.Complete(CreateOpenLoopReports());
        var sample = new ScalingSample("CurrentDisabled", 3, 3, "stable", 0, 3, 9,
            new("candidate", "same"), System.Text.Json.JsonSerializer.SerializeToElement(new { ProcessorCount = 4 }),
            90, 900, 9000, 90, 1000, [], "OpenLoopSynchronized", summary);
        var enabled = sample with { RuntimePath = "CurrentEnabledSupported" };
        var pair = Assert.Single(ScalingComparison.Create(System.Text.Json.JsonSerializer.Serialize(new[] { sample, enabled })).Pairs);
        Assert.Equal("OpenLoopSynchronized", pair.Workload);
        Assert.Equal(102, pair.Disabled.MedianConvergenceMilliseconds);
        Assert.Equal(10, pair.Disabled.RpcsPerPublication);
        enabled = enabled with { Workload = "OpenLoopStaggered" };
        Assert.Throws<InvalidOperationException>(() => ScalingComparison.Create(System.Text.Json.JsonSerializer.Serialize(new[] { sample, enabled })));
    }

    private static OpenLoopNodeReport[] CreateOpenLoopReports()
    {
        var epoch = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, 3).Select(index => new OpenLoopNodeReport(10 + index, $"node-{index}",
            new(new(epoch, 3, index, 3, "OpenLoopSynchronized"),
                Enumerable.Range(0, 3).Select(sequence =>
                {
                    var start = epoch.AddSeconds(sequence);
                    return new OpenLoopPublication(sequence, start, new(start, start.AddMilliseconds(10), start.UtcTicks, $"{index}/{sequence}"));
                }).ToArray(), 0, 0))).ToArray();
    }

    private static OpenLoopMeasurements CreateOpenLoopMeasurements() =>
        new(Enumerable.Range(0, 3).ToDictionary(index => 10 + index, index => $"node-{index}"),
            Enumerable.Range(0, 3).ToDictionary(index => $"node-{index}", _ => 0L), 3);

    private static void ObserveOpenLoop(OpenLoopMeasurements measurements, OpenLoopNodeReport[] reports, int sequence, string? mismatch = null)
    {
        var values = reports.ToDictionary(report => report.Address, report => report.Report.Publications[sequence].Result);
        var versions = values.ToDictionary(pair => pair.Key, pair => pair.Value.Version);
        var load = values.ToDictionary(pair => pair.Key, pair => pair.Value.Value);
        if (mismatch == "value")
        {
            load["node-0"] = "wrong";
        }
        else if (mismatch == "version")
        {
            versions["node-0"]++;
        }
        for (var index = 0; index < 3; index++)
        {
            measurements.Observe(10 + index, values["node-0"].StartedAtUtc.AddMilliseconds(100 + index), versions, load);
        }
    }

    private sealed class RecordingClock : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        private readonly Channel<TimeSpan> _delays = Channel.CreateUnbounded<TimeSpan>();
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _inner.CreateTimer(callback, state, dueTime, period);
            Assert.True(_delays.Writer.TryWrite(dueTime));
            return timer;
        }
        public ValueTask<TimeSpan> NextDelay() => _delays.Reader.ReadAsync(TestContext.Current.CancellationToken);
        public void Advance(TimeSpan delta) => _inner.Advance(delta);
    }
}
