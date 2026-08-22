using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime.MembershipService;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Membership;

[TestCategory("BVT"), TestCategory("Membership")]
public class LocalSiloHealthEventHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _timeProvider = new(Start);
    private readonly long _startTimestamp;

    public LocalSiloHealthEventHistoryTests()
    {
        _startTimestamp = _timeProvider.GetTimestamp();
    }

    [Fact]
    public void Add_GroupsReportsBySecond()
    {
        var history = CreateHistory();
        var start = _timeProvider.GetTimestamp();

        history.Add(Event(start, LocalSiloHealthCheckKind.RuntimeStall, source: "first"));
        history.Add(Event(TimestampAt(TimeSpan.FromMilliseconds(500)), LocalSiloHealthCheckKind.RuntimeStall, source: "second"));

        Assert.Equal(2, history.Count);
        Assert.Equal(1, history.OccupiedBucketCount);

        history.Add(Event(TimestampAt(TimeSpan.FromSeconds(1)), LocalSiloHealthCheckKind.RuntimeStall, source: "third"));

        Assert.Equal(3, history.Count);
        Assert.Equal(2, history.OccupiedBucketCount);
    }

    [Fact]
    public void Aggregate_ReusesExpiredBucketSlots()
    {
        var history = CreateHistory();
        var start = _timeProvider.GetTimestamp();
        history.Add(Event(start, LocalSiloHealthCheckKind.RuntimeStall, source: "expired"));

        _timeProvider.Advance(TimeSpan.FromSeconds(61));
        var now = _timeProvider.GetTimestamp();
        history.Add(Event(now, LocalSiloHealthCheckKind.RuntimeStall, source: "current"));

        var status = history.Aggregate(
            TimestampAt(TimeSpan.FromSeconds(1)),
            now,
            now,
            LocalSiloHealthCheckCategory.Local,
            LocalSiloHealthMonitor.MaxScore);

        var healthEvent = Assert.Single(status.Events);
        Assert.Equal("current", healthEvent.Source);
        Assert.Equal(1, history.Count);
        Assert.Equal(1, history.OccupiedBucketCount);
    }

    [Fact]
    public void Aggregate_CompactsPartiallyExpiredBucketWithUnorderedReports()
    {
        var history = CreateHistory();
        history.Add(Event(
            TimestampAt(TimeSpan.FromMilliseconds(500)),
            LocalSiloHealthCheckKind.RuntimeStall,
            source: "retained"));
        history.Add(Event(
            TimestampAt(TimeSpan.FromMilliseconds(100)),
            LocalSiloHealthCheckKind.RuntimeStall,
            source: "expired"));

        var now = TimestampAt(TimeSpan.FromSeconds(60.25));
        var status = history.Aggregate(
            TimestampAt(TimeSpan.FromMilliseconds(250)),
            now,
            now,
            LocalSiloHealthCheckCategory.Local,
            LocalSiloHealthMonitor.MaxScore);

        var healthEvent = Assert.Single(status.Events);
        Assert.Equal("retained", healthEvent.Source);
        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void Aggregate_SelectsWorstReportPerIdentityAcrossBuckets()
    {
        var history = CreateHistory();
        history.Add(Event(
            TimestampAt(TimeSpan.Zero),
            LocalSiloHealthCheckKind.RuntimeStall,
            score: 1,
            source: "runtime"));
        history.Add(Event(
            TimestampAt(TimeSpan.FromSeconds(1)),
            LocalSiloHealthCheckKind.RuntimeStall,
            score: 4,
            source: "runtime"));
        history.Add(Event(
            TimestampAt(TimeSpan.FromSeconds(2)),
            LocalSiloHealthCheckKind.RuntimeStall,
            score: 2,
            source: "runtime"));

        var end = TimestampAt(TimeSpan.FromSeconds(2));
        var status = history.Aggregate(
            TimestampAt(TimeSpan.Zero),
            end,
            end,
            LocalSiloHealthCheckCategory.Local,
            LocalSiloHealthMonitor.MaxScore);

        var healthEvent = Assert.Single(status.Events);
        Assert.Equal(4, status.Score);
        Assert.Equal(TimestampAt(TimeSpan.FromSeconds(1)), healthEvent.Timestamp);
    }

    [Fact]
    public void Aggregate_CarriesStateButNotIncidentsIntoInterval()
    {
        var history = CreateHistory();
        history.Add(Event(
            TimestampAt(TimeSpan.Zero),
            LocalSiloHealthCheckKind.HealthCheckParticipant,
            score: 1,
            source: "participant"));
        history.Add(Event(
            TimestampAt(TimeSpan.Zero),
            LocalSiloHealthCheckKind.RuntimeStall,
            score: 2,
            source: "runtime"));

        var start = TimestampAt(TimeSpan.FromMilliseconds(500));
        var end = TimestampAt(TimeSpan.FromSeconds(1));
        var status = history.Aggregate(
            start,
            end,
            end,
            LocalSiloHealthCheckCategory.Local,
            LocalSiloHealthMonitor.MaxScore);

        var healthEvent = Assert.Single(status.Events);
        Assert.Equal(LocalSiloHealthCheckKind.HealthCheckParticipant, healthEvent.Kind);
        Assert.Equal(1, status.Score);
    }

    [Fact]
    public void Aggregate_ClampsWithoutOverflow()
    {
        var history = CreateHistory();
        var timestamp = TimestampAt(TimeSpan.Zero);
        history.Add(Event(
            timestamp,
            LocalSiloHealthCheckKind.RuntimeStall,
            score: int.MaxValue,
            source: "runtime"));
        history.Add(Event(
            timestamp,
            LocalSiloHealthCheckKind.ComponentHealthCheckStall,
            score: 1,
            source: "component"));

        var status = history.Aggregate(
            timestamp,
            timestamp,
            timestamp,
            LocalSiloHealthCheckCategory.Local,
            LocalSiloHealthMonitor.MaxScore);

        Assert.Equal(LocalSiloHealthMonitor.MaxScore, status.Score);
    }

    private LocalSiloHealthEventHistory CreateHistory()
        => new(_timeProvider, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1));

    private LocalSiloHealthEvent Event(
        long timestamp,
        LocalSiloHealthCheckKind kind,
        int score = 1,
        string? source = null)
        => new(
            timestamp,
            kind,
            LocalSiloHealthCheckCategory.Local,
            source,
            score,
            Complaint: null,
            Duration: null);

    private long TimestampAt(TimeSpan elapsed)
        => _startTimestamp
            + (long)(elapsed.TotalSeconds * _timeProvider.TimestampFrequency);
}
