using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NonSilo.Tests.Utilities;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Membership
{
    [TestCategory("BVT"), TestCategory("Membership")]
    public class LocalSiloHealthMonitorTests
    {
        private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        private readonly FakeTimeProvider _timeProvider = new(Start);
        private readonly long _startTimestamp;
        private readonly IMembershipManager _membershipManager;
        private readonly IProbeHealthMonitor _probeHealthMonitor = Substitute.For<IProbeHealthMonitor>();
        private readonly ILocalSiloDetails _localSiloDetails = Substitute.For<ILocalSiloDetails>();

        public LocalSiloHealthMonitorTests()
        {
            _startTimestamp = _timeProvider.GetTimestamp();
            var localSilo = SiloAddress.FromParsableString("127.0.0.1:100@100");
            _localSiloDetails.SiloAddress.Returns(localSilo);
            _membershipManager = Substitute.For<IMembershipManager>();
            _membershipManager.CurrentSnapshot.Returns(new MembershipTableSnapshot(
                new MembershipVersion(1),
                ImmutableDictionary<SiloAddress, MembershipEntry>.Empty.Add(
                    localSilo,
                    new MembershipEntry
                    {
                        SiloAddress = localSilo,
                        Status = SiloStatus.Active,
                        StartTime = Start.UtcDateTime,
                        IAmAliveTime = Start.UtcDateTime,
                    })));
        }

        [Fact]
        public void GetTimestamp_UsesMembershipTimeProvider()
        {
            var monitor = CreateMonitor();

            Assert.Equal(_startTimestamp, monitor.GetTimestamp());

            _timeProvider.Advance(TimeSpan.FromMilliseconds(250));

            Assert.Equal(TimestampAt(TimeSpan.FromMilliseconds(250)), monitor.GetTimestamp());
        }

        [Fact]
        public void GetLocalHealthStatus_RejectsNegativePeriod()
        {
            var participant = new TestHealthCheckParticipant(_ => (true, null));
            var monitor = CreateMonitor(participant);

            var exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => monitor.GetLocalHealthStatus(TimeSpan.FromTicks(-1), LocalSiloHealthCheckCategory.All));

            Assert.Equal("period", exception.ParamName);
            Assert.Equal(0, participant.CallCount);
        }

        [Fact]
        public void GetLocalHealthStatus_RejectsReversedInterval()
        {
            var participant = new TestHealthCheckParticipant(_ => (true, null));
            var monitor = CreateMonitor(participant);

            var exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => monitor.GetLocalHealthStatus(
                    _startTimestamp,
                    _startTimestamp - 1,
                    LocalSiloHealthCheckCategory.All));

            Assert.Equal("endTimestamp", exception.ParamName);
            Assert.Equal(0, participant.CallCount);
        }

        [Fact]
        public void GetLocalHealthStatus_RetentionAndQueryBoundsAreInclusive()
        {
            var monitor = CreateMonitor();
            var recorder = (ILocalSiloHealthEventRecorder)monitor;
            recorder.RecordHealthEvent(
                LocalSiloHealthCheckKind.RuntimeStall,
                score: 3,
                complaint: "runtime stalled",
                duration: TimeSpan.FromSeconds(3),
                source: "retention-boundary");

            var atRecordedTime = monitor.GetLocalHealthStatus(TimeSpan.Zero, LocalSiloHealthCheckCategory.Local);
            Assert.Equal(_startTimestamp, Assert.Single(atRecordedTime.Events, IsRetentionEvent).Timestamp);

            _timeProvider.Advance(TimeSpan.FromMinutes(1));
            var atRetentionBoundary = monitor.GetLocalHealthStatus(TimeSpan.FromMinutes(1), LocalSiloHealthCheckCategory.Local);
            Assert.Contains(atRetentionBoundary.Events, IsRetentionEvent);

            _timeProvider.Advance(TimeSpan.FromTicks(1));
            var afterRetentionBoundary = monitor.GetLocalHealthStatus(TimeSpan.FromMinutes(1), LocalSiloHealthCheckCategory.Local);
            Assert.DoesNotContain(afterRetentionBoundary.Events, IsRetentionEvent);

            static bool IsRetentionEvent(LocalSiloHealthEvent item)
                => item.Kind == LocalSiloHealthCheckKind.RuntimeStall
                    && item.Source == "retention-boundary";
        }

        [Fact]
        public void GetLocalHealthStatus_LimitsLookbackToRetainedHistory()
        {
            var monitor = CreateMonitor();
            var recorder = (ILocalSiloHealthEventRecorder)monitor;
            recorder.RecordHealthEvent(
                LocalSiloHealthCheckKind.RuntimeStall,
                score: 2,
                complaint: "retained",
                duration: TimeSpan.FromSeconds(2));
            _timeProvider.Advance(TimeSpan.FromSeconds(59));

            var status = monitor.GetLocalHealthStatus(TimeSpan.MaxValue, LocalSiloHealthCheckCategory.Local);

            Assert.Contains(
                status.Events,
                item => item.Kind == LocalSiloHealthCheckKind.RuntimeStall
                    && item.Complaint == "retained");
        }

        [Fact]
        public void GetLocalHealthStatus_SelectsWorstEventPerKindAndSource()
        {
            var monitor = CreateMonitor();
            var recorder = (ILocalSiloHealthEventRecorder)monitor;
            recorder.RecordHealthEvent(LocalSiloHealthCheckKind.RuntimeStall, 1, "alpha-low", TimeSpan.FromSeconds(1), "alpha");
            recorder.RecordHealthEvent(LocalSiloHealthCheckKind.RuntimeStall, 4, "alpha-high", TimeSpan.FromSeconds(4), "alpha");
            recorder.RecordHealthEvent(LocalSiloHealthCheckKind.RuntimeStall, 2, "alpha-later-low", TimeSpan.FromSeconds(2), "alpha");
            recorder.RecordHealthEvent(LocalSiloHealthCheckKind.RuntimeStall, 3, "beta-high", TimeSpan.FromSeconds(3), "beta");

            var status = monitor.GetLocalHealthStatus(TimeSpan.Zero, LocalSiloHealthCheckCategory.Local);
            var selected = status.Events.Where(item => item.Kind == LocalSiloHealthCheckKind.RuntimeStall).ToArray();

            Assert.Equal(7, status.Score);
            Assert.Collection(
                selected,
                item =>
                {
                    Assert.Equal("alpha", item.Source);
                    Assert.Equal(4, item.Score);
                    Assert.Equal("alpha-high", item.Complaint);
                    Assert.Equal(TimeSpan.FromSeconds(4), item.Duration);
                },
                item =>
                {
                    Assert.Equal("beta", item.Source);
                    Assert.Equal(3, item.Score);
                    Assert.Equal("beta-high", item.Complaint);
                    Assert.Equal(TimeSpan.FromSeconds(3), item.Duration);
                });
        }

        [Fact]
        public void GetLocalHealthStatus_EqualScoresSelectLatestEvent()
        {
            var monitor = CreateMonitor();
            var recorder = (ILocalSiloHealthEventRecorder)monitor;
            recorder.RecordHealthEvent(
                LocalSiloHealthCheckKind.RuntimeStall,
                score: 3,
                complaint: "earlier",
                duration: TimeSpan.FromSeconds(1),
                source: "equal-score");
            _timeProvider.Advance(TimeSpan.FromSeconds(1));
            recorder.RecordHealthEvent(
                LocalSiloHealthCheckKind.RuntimeStall,
                score: 3,
                complaint: "later",
                duration: TimeSpan.FromSeconds(2),
                source: "equal-score");

            var status = monitor.GetLocalHealthStatus(TimeSpan.FromMinutes(1), LocalSiloHealthCheckCategory.Local);
            var selected = Assert.Single(status.Events, item => item.Source == "equal-score");

            Assert.Equal(3, status.Score);
            Assert.Equal(TimestampAt(TimeSpan.FromSeconds(1)), selected.Timestamp);
            Assert.Equal("later", selected.Complaint);
            Assert.Equal(TimeSpan.FromSeconds(2), selected.Duration);
        }

        [Fact]
        public void GetLocalHealthStatus_FiltersCategoriesAndClampsAggregateScore()
        {
            var monitor = CreateMonitor();
            var recorder = (ILocalSiloHealthEventRecorder)monitor;
            var allKinds = Enum.GetValues<LocalSiloHealthCheckKind>();
            foreach (var kind in allKinds)
            {
                recorder.RecordHealthEvent(kind, score: 2, complaint: kind.ToString(), source: $"recorded-{kind}");
            }

            var network = monitor.GetLocalHealthStatus(TimeSpan.Zero, LocalSiloHealthCheckCategory.Network);
            var local = monitor.GetLocalHealthStatus(TimeSpan.Zero, LocalSiloHealthCheckCategory.Local);
            var none = monitor.GetLocalHealthStatus(TimeSpan.Zero, LocalSiloHealthCheckCategory.None);
            var all = monitor.GetLocalHealthStatus(TimeSpan.Zero, LocalSiloHealthCheckCategory.All);

            LocalSiloHealthCheckKind[] networkKinds =
            [
                LocalSiloHealthCheckKind.MembershipStatus,
                LocalSiloHealthCheckKind.SiloSuspected,
                LocalSiloHealthCheckKind.ProbeRequests,
                LocalSiloHealthCheckKind.ProbeResponses,
            ];
            var localKinds = allKinds.Except(networkKinds).ToArray();

            Assert.Equal(networkKinds, RecordedKinds(network));
            Assert.All(network.Events, item => Assert.Equal(LocalSiloHealthCheckCategory.Network, item.Category));
            Assert.Equal(LocalSiloHealthMonitor.MaxScore, network.Score);

            Assert.Equal(localKinds, RecordedKinds(local));
            Assert.All(local.Events, item => Assert.Equal(LocalSiloHealthCheckCategory.Local, item.Category));
            Assert.Equal(LocalSiloHealthMonitor.MaxScore, local.Score);

            Assert.Equal(0, none.Score);
            Assert.Empty(none.Events);

            Assert.Equal(allKinds, RecordedKinds(all));
            Assert.Equal(LocalSiloHealthMonitor.MaxScore, all.Score);
            Assert.Contains(all.Events, item => item.Category == LocalSiloHealthCheckCategory.Local);
            Assert.Contains(all.Events, item => item.Category == LocalSiloHealthCheckCategory.Network);

            static LocalSiloHealthCheckKind[] RecordedKinds(LocalSiloHealthStatus status)
                => status.Events
                    .Where(item => item.Source?.StartsWith("recorded-", StringComparison.Ordinal) == true)
                    .Select(item => item.Kind)
                    .ToArray();
        }

        [Fact]
        public void GetLocalHealthStatus_LocalCacheDoesNotSuppressNetworkCheck()
        {
            var monitor = CreateMonitor();
            _ = monitor.GetLocalHealthStatus(TimeSpan.Zero, LocalSiloHealthCheckCategory.Local);

            var network = monitor.GetLocalHealthStatus(TimeSpan.Zero, LocalSiloHealthCheckCategory.Network);

            var membershipEvent = Assert.Single(
                network.Events,
                item => item.Kind == LocalSiloHealthCheckKind.MembershipStatus);
            Assert.Equal(LocalSiloHealthCheckCategory.Network, membershipEvent.Category);
            Assert.Equal(0, membershipEvent.Score);
        }

        [Fact]
        public void GetLocalHealthStatus_MembershipRecoveryReplacesPriorState()
        {
            var localSilo = _localSiloDetails.SiloAddress;
            var membershipEntry = new MembershipEntry
            {
                SiloAddress = localSilo,
                Status = SiloStatus.Joining,
                StartTime = Start.UtcDateTime,
                IAmAliveTime = Start.UtcDateTime,
            };
            _membershipManager.CurrentSnapshot.Returns(new MembershipTableSnapshot(
                new MembershipVersion(1),
                ImmutableDictionary<SiloAddress, MembershipEntry>.Empty.Add(localSilo, membershipEntry)));
            var monitor = CreateMonitor();
            var joining = monitor.GetLocalHealthStatus(TimeSpan.Zero, LocalSiloHealthCheckCategory.Network);
            Assert.Equal(LocalSiloHealthMonitor.MaxScore, joining.Score);

            _timeProvider.Advance(TimeSpan.FromSeconds(1));
            membershipEntry.Status = SiloStatus.Active;
            var active = monitor.GetLocalHealthStatus(TimeSpan.Zero, LocalSiloHealthCheckCategory.Network);
            Assert.Equal(0, active.Score);

            _timeProvider.Advance(TimeSpan.FromMilliseconds(500));
            var carried = monitor.GetLocalHealthStatus(
                TimestampAt(TimeSpan.FromSeconds(1)),
                TimestampAt(TimeSpan.FromSeconds(1.5)),
                LocalSiloHealthCheckCategory.Network);
            Assert.Equal(0, carried.Score);
            var membershipStatus = Assert.Single(
                carried.Events,
                item => item.Kind == LocalSiloHealthCheckKind.MembershipStatus);
            Assert.Equal(0, membershipStatus.Score);
            Assert.Null(membershipStatus.Source);
            Assert.Null(membershipStatus.Complaint);
        }

        [Fact]
        public void GetLocalHealthStatus_PreservesDistinctStallKinds()
        {
            var monitor = CreateMonitor();
            var recorder = (ILocalSiloHealthEventRecorder)monitor;
            recorder.RecordHealthEvent(
                LocalSiloHealthCheckKind.GarbageCollectionPause,
                score: 1,
                complaint: "gc pause",
                duration: TimeSpan.FromSeconds(1));
            recorder.RecordHealthEvent(
                LocalSiloHealthCheckKind.RuntimeStall,
                score: 2,
                complaint: "runtime stall",
                duration: TimeSpan.FromSeconds(2));
            recorder.RecordHealthEvent(
                LocalSiloHealthCheckKind.ComponentHealthCheckStall,
                score: 3,
                complaint: "component stall",
                duration: TimeSpan.FromSeconds(3));

            var status = monitor.GetLocalHealthStatus(TimeSpan.Zero, LocalSiloHealthCheckCategory.Local);
            var stalls = status.Events
                .Where(item => item.Kind is LocalSiloHealthCheckKind.GarbageCollectionPause
                    or LocalSiloHealthCheckKind.RuntimeStall
                    or LocalSiloHealthCheckKind.ComponentHealthCheckStall)
                .ToArray();

            Assert.Equal(6, status.Score);
            Assert.Collection(
                stalls,
                item => AssertStall(item, LocalSiloHealthCheckKind.GarbageCollectionPause, 1, "gc pause", TimeSpan.FromSeconds(1)),
                item => AssertStall(item, LocalSiloHealthCheckKind.RuntimeStall, 2, "runtime stall", TimeSpan.FromSeconds(2)),
                item => AssertStall(item, LocalSiloHealthCheckKind.ComponentHealthCheckStall, 3, "component stall", TimeSpan.FromSeconds(3)));
            Assert.Equal(new[] { "gc pause", "runtime stall", "component stall" }, status.Complaints);

            static void AssertStall(
                LocalSiloHealthEvent item,
                LocalSiloHealthCheckKind kind,
                int score,
                string complaint,
                TimeSpan duration)
            {
                Assert.Equal(kind, item.Kind);
                Assert.Equal(LocalSiloHealthCheckCategory.Local, item.Category);
                Assert.Null(item.Source);
                Assert.Equal(score, item.Score);
                Assert.Equal(complaint, item.Complaint);
                Assert.Equal(duration, item.Duration);
            }
        }

        [Fact]
        public void GetLocalHealthStatus_IncludesDurationEventWhichOverlapsInterval()
        {
            var monitor = CreateMonitor();
            var recorder = (ILocalSiloHealthEventRecorder)monitor;
            _timeProvider.Advance(TimeSpan.FromSeconds(3));
            recorder.RecordHealthEvent(
                LocalSiloHealthCheckKind.RuntimeStall,
                score: 2,
                complaint: "overlapping runtime stall",
                duration: TimeSpan.FromSeconds(2));

            var status = monitor.GetLocalHealthStatus(
                _startTimestamp,
                TimestampAt(TimeSpan.FromSeconds(2)),
                LocalSiloHealthCheckCategory.Local);

            var healthEvent = Assert.Single(
                status.Events,
                item => item.Kind == LocalSiloHealthCheckKind.RuntimeStall);
            Assert.Equal(4, status.Score);
            Assert.Equal(TimestampAt(TimeSpan.FromSeconds(3)), healthEvent.Timestamp);
            Assert.Equal(TimeSpan.FromSeconds(2), healthEvent.Duration);
            var stallEvent = Assert.Single(
                status.Events,
                item => item.Kind == LocalSiloHealthCheckKind.ThreadPoolStall);
            Assert.Equal(2, stallEvent.Score);
            Assert.Equal(TimeSpan.FromMilliseconds(2900), stallEvent.Duration);
        }

        [Fact]
        public void GetLocalHealthStatus_CarriesStateSampleIntoInterval()
        {
            var participant = new TestHealthCheckParticipant(_ => (false, "persistently unhealthy"));
            var monitor = CreateMonitor(participant);
            _ = monitor.GetLocalHealthStatus(TimeSpan.Zero, LocalSiloHealthCheckCategory.Local);
            _timeProvider.Advance(TimeSpan.FromMilliseconds(500));

            var status = monitor.GetLocalHealthStatus(
                TimestampAt(TimeSpan.FromMilliseconds(250)),
                TimestampAt(TimeSpan.FromMilliseconds(500)),
                LocalSiloHealthCheckCategory.Local);

            var participantEvent = Assert.Single(
                status.Events,
                item => item.Kind == LocalSiloHealthCheckKind.HealthCheckParticipant);
            Assert.Equal(1, status.Score);
            Assert.Equal(_startTimestamp, participantEvent.Timestamp);
            Assert.Contains("persistently unhealthy", participantEvent.Complaint, StringComparison.Ordinal);
        }

        [Fact]
        public void GetLocalHealthStatus_DoesNotCarryIncidentIntoLaterInterval()
        {
            var monitor = CreateMonitor();
            var recorder = (ILocalSiloHealthEventRecorder)monitor;
            recorder.RecordHealthEvent(
                LocalSiloHealthCheckKind.RuntimeStall,
                score: 2,
                complaint: "completed runtime stall",
                duration: TimeSpan.FromSeconds(1));
            _timeProvider.Advance(TimeSpan.FromSeconds(2));

            var status = monitor.GetLocalHealthStatus(
                TimestampAt(TimeSpan.FromSeconds(1)),
                TimestampAt(TimeSpan.FromSeconds(2)),
                LocalSiloHealthCheckCategory.Local);

            Assert.DoesNotContain(
                status.Events,
                item => item.Kind == LocalSiloHealthCheckKind.RuntimeStall);
        }

        [Fact]
        public void GetLocalHealthStatus_UsesOneSecondOnDemandCacheCadence()
        {
            var participant = new TestHealthCheckParticipant(_ => (true, null));
            var monitor = CreateMonitor(participant);

            var first = monitor.GetLocalHealthStatus(TimeSpan.FromMinutes(1), LocalSiloHealthCheckCategory.Local);
            Assert.Equal(1, participant.CallCount);

            _timeProvider.Advance(TimeSpan.FromMilliseconds(999));
            var cached = monitor.GetLocalHealthStatus(TimeSpan.FromMinutes(1), LocalSiloHealthCheckCategory.Local);
            Assert.Equal(1, participant.CallCount);

            _timeProvider.Advance(TimeSpan.FromMilliseconds(1));
            var refreshed = monitor.GetLocalHealthStatus(TimeSpan.FromMinutes(1), LocalSiloHealthCheckCategory.Local);
            Assert.Equal(2, participant.CallCount);

            Assert.Equal(_startTimestamp, Assert.Single(first.Events, IsParticipantEvent).Timestamp);
            Assert.Equal(_startTimestamp, Assert.Single(cached.Events, IsParticipantEvent).Timestamp);
            Assert.Equal(TimestampAt(TimeSpan.FromSeconds(1)), Assert.Single(refreshed.Events, IsParticipantEvent).Timestamp);

            static bool IsParticipantEvent(LocalSiloHealthEvent item)
                => item.Kind == LocalSiloHealthCheckKind.HealthCheckParticipant;
        }

        [Fact]
        public void LocalSiloHealthMonitor_SchedulesHealthChecksOncePerSecond()
        {
            TimeSpan? period = null;
            var timerFactory = new DelegateAsyncTimerFactory(
                (value, _) =>
                {
                    period = value;
                    return new DelegateAsyncTimer(_ => Task.FromResult(false));
                });

            _ = CreateMonitor(timerFactory);

            Assert.Equal(TimeSpan.FromSeconds(1), period);
        }

        [Fact]
        public async Task GetLocalHealthStatus_ConcurrentCallersShareOneNonOverlappingParticipantPass()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var activeCalls = 0;
            var maximumActiveCalls = 0;
            var participant = new TestHealthCheckParticipant(_ =>
            {
                var active = Interlocked.Increment(ref activeCalls);
                UpdateMaximum(ref maximumActiveCalls, active);
                entered.TrySetResult();
                try
                {
                    release.Task.GetAwaiter().GetResult();
                    return (true, null);
                }
                finally
                {
                    Interlocked.Decrement(ref activeCalls);
                }
            });
            var monitor = CreateMonitor(participant);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = Enumerable.Range(0, 8)
                .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();
            var workers = Enumerable.Range(0, ready.Length)
                .Select(index => Task.Run(async () =>
                {
                    ready[index].SetResult();
                    await start.Task;
                    return monitor.GetLocalHealthStatus(TimeSpan.Zero, LocalSiloHealthCheckCategory.Local);
                }))
                .ToArray();

            await Task.WhenAll(ready.Select(item => item.Task));
            start.SetResult();
            await entered.Task;
            try
            {
                Assert.Equal(1, participant.CallCount);
                Assert.Equal(1, Volatile.Read(ref activeCalls));
                Assert.Equal(1, Volatile.Read(ref maximumActiveCalls));
                Assert.All(workers, worker => Assert.False(worker.IsCompleted));
            }
            finally
            {
                release.TrySetResult();
            }

            var results = await Task.WhenAll(workers);
            var expected = results[0];
            var expectedEvents = expected.Events
                .Select(item => (item.Timestamp, item.Kind, item.Category, item.Source, item.Score, item.Complaint, item.Duration))
                .ToArray();

            Assert.Equal(1, participant.CallCount);
            Assert.Equal(0, Volatile.Read(ref activeCalls));
            Assert.Equal(1, Volatile.Read(ref maximumActiveCalls));
            Assert.All(results, result =>
            {
                Assert.Equal(expected.Score, result.Score);
                Assert.Equal(
                    expectedEvents,
                    result.Events.Select(item => (item.Timestamp, item.Kind, item.Category, item.Source, item.Score, item.Complaint, item.Duration)));
                var participantEvent = Assert.Single(result.Events, item => item.Kind == LocalSiloHealthCheckKind.HealthCheckParticipant);
                Assert.Equal(_startTimestamp, participantEvent.Timestamp);
                Assert.Equal(0, participantEvent.Score);
                Assert.Null(participantEvent.Complaint);
            });
        }

        [Fact]
        public async Task RecordHealthEvent_DoesNotWaitForHealthCheckParticipants()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var participant = new TestHealthCheckParticipant(_ =>
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return (true, null);
            });
            var monitor = CreateMonitor(participant);
            var checkTask = Task.Run(
                () => monitor.GetLocalHealthStatus(TimeSpan.Zero, LocalSiloHealthCheckCategory.Local));

            await entered.Task;
            try
            {
                var recordTask = Task.Run(
                    () => ((ILocalSiloHealthEventRecorder)monitor).RecordHealthEvent(
                        LocalSiloHealthCheckKind.ComponentHealthCheckStall,
                        score: 1,
                        complaint: "participant check stalled",
                        duration: TimeSpan.FromSeconds(2)));

                await recordTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(checkTask.IsCompleted);
            }
            finally
            {
                release.TrySetResult();
            }

            await checkTask;
            var status = monitor.GetLocalHealthStatus(TimeSpan.Zero, LocalSiloHealthCheckCategory.Local);
            Assert.Contains(
                status.Events,
                item => item.Kind == LocalSiloHealthCheckKind.ComponentHealthCheckStall
                    && item.Complaint == "participant check stalled");
        }

        [Fact]
        public void GetLocalHealthStatus_EmitsTypedThreadPoolStallEvent()
        {
            var monitor = CreateMonitor();

            var status = monitor.GetLocalHealthStatus(TimeSpan.Zero, LocalSiloHealthCheckCategory.Local);
            var threadPoolEvent = Assert.Single(
                status.Events,
                item => item.Kind == LocalSiloHealthCheckKind.ThreadPoolStall);

            Assert.Equal(_startTimestamp, threadPoolEvent.Timestamp);
            Assert.Equal(LocalSiloHealthCheckCategory.Local, threadPoolEvent.Category);
            Assert.Null(threadPoolEvent.Source);
            Assert.True(threadPoolEvent.Score >= 0);
            Assert.NotNull(threadPoolEvent.Duration);
            Assert.True(threadPoolEvent.Duration >= TimeSpan.Zero);
        }

        [Fact]
        public void RecordHealthEvent_RejectsNegativeScore()
        {
            var monitor = CreateMonitor();
            var recorder = (ILocalSiloHealthEventRecorder)monitor;

            var exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => recorder.RecordHealthEvent(
                    LocalSiloHealthCheckKind.RuntimeStall,
                    score: -1,
                    complaint: "rejected",
                    duration: TimeSpan.FromSeconds(1),
                    source: "negative-score"));
            var status = monitor.GetLocalHealthStatus(TimeSpan.Zero, LocalSiloHealthCheckCategory.All);

            Assert.Equal("score", exception.ParamName);
            Assert.DoesNotContain(status.Events, item => item.Source == "negative-score");
            Assert.DoesNotContain(status.Complaints, complaint => complaint == "rejected");
        }

        [Fact]
        public void Dispose_DisposesDegradationTimer()
        {
            var timer = new DelegateAsyncTimer(_ => Task.FromResult(false));
            var monitor = CreateMonitor(
                new DelegateAsyncTimerFactory((_, _) => timer));

            monitor.Dispose();

            Assert.Equal(1, timer.DisposedCounter);
        }

        private LocalSiloHealthMonitor CreateMonitor(params IHealthCheckParticipant[] participants)
        {
            var timerFactory = new DelegateAsyncTimerFactory(
                (_, _) => new DelegateAsyncTimer(_ => Task.FromResult(false)));
            return CreateMonitor(timerFactory, participants);
        }

        private LocalSiloHealthMonitor CreateMonitor(
            IAsyncTimerFactory timerFactory,
            params IHealthCheckParticipant[] participants)
        {
            return new LocalSiloHealthMonitor(
                participants,
                _membershipManager,
                _probeHealthMonitor,
                _localSiloDetails,
                NullLogger<LocalSiloHealthMonitor>.Instance,
                Options.Create(new ClusterMembershipOptions()),
                timerFactory,
                NullLoggerFactory.Instance,
                _timeProvider);
        }

        private long TimestampAt(TimeSpan elapsed)
            => _startTimestamp + (long)(elapsed.TotalSeconds * _timeProvider.TimestampFrequency);

        private static void UpdateMaximum(ref int maximum, int candidate)
        {
            var observed = Volatile.Read(ref maximum);
            while (candidate > observed)
            {
                var prior = Interlocked.CompareExchange(ref maximum, candidate, observed);
                if (prior == observed)
                {
                    return;
                }

                observed = prior;
            }
        }

        private sealed class TestHealthCheckParticipant(
            Func<DateTime, (bool IsHealthy, string? Reason)> check) : IHealthCheckParticipant
        {
            private int _callCount;

            public int CallCount => Volatile.Read(ref _callCount);

            public bool CheckHealth(DateTime lastCheckTime, [NotNullWhen(false)] out string? reason)
            {
                Interlocked.Increment(ref _callCount);
                var result = check(lastCheckTime);
                reason = result.Reason;
                return result.IsHealthy;
            }
        }
    }
}
