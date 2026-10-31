using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Xunit;

namespace Tester.Diagnostics;

public class WatchdogTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Theory, TestCategory("BVT")]
    [InlineData(false)]
    [InlineData(true)]
    public void Stop_ReleasesCancellationResourcesAfterPartialStartup(bool unstartedThreads)
    {
        using var fixture = new WatchdogFixture();
        var source = fixture.Cancellation;
        var waitHandle = source.Token.WaitHandle;
        if (unstartedThreads)
        {
            fixture.SetField("_componentWatchdogThread", new Thread(static () => { }));
            fixture.SetField("_platformWatchdogThread", new Thread(static () => { }));
        }

        fixture.Stop();

        Assert.Throws<ObjectDisposedException>(() => source.Token);
        Assert.Throws<ObjectDisposedException>(() => waitHandle.WaitOne(0));
        fixture.Stop();
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task Stop_JoinsRunningHealthCheckBeforeReleasingCancellationResources()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new WatchdogFixture(participants: [new BlockingHealthParticipant(checking, release)]);
        var source = fixture.Cancellation;
        var token = source.Token;
        var waitHandle = token.WaitHandle;
        using var registration = token.Register(() => cancelled.TrySetResult());
        Task? firstStop = null;
        Task? secondStop = null;
        try
        {
            fixture.StartWatchdog();
            await checking.Task.WaitAsync(timeout.Token);
            firstStop = Task.Run(fixture.Stop, TestContext.Current.CancellationToken);
            await cancelled.Task.WaitAsync(timeout.Token);
            secondStop = Task.Run(fixture.Stop, TestContext.Current.CancellationToken);
            Assert.True(waitHandle.WaitOne(0));
            Assert.False(firstStop.IsCompleted);

            release.SetResult();
            await Task.WhenAll(firstStop, secondStop).WaitAsync(timeout.Token);

            Assert.Throws<ObjectDisposedException>(() => source.Token);
            Assert.Throws<ObjectDisposedException>(() => waitHandle.WaitOne(0));
            fixture.Stop();
        }
        finally
        {
            release.TrySetResult();
            if (firstStop is not null) await firstStop;
            if (secondStop is not null) await secondStop;
        }
    }

    [Fact, TestCategory("BVT")]
    public void CheckRuntimeHealth_RecordsGarbageCollectionPauseEvent()
    {
        using var fixture = new WatchdogFixture();
        fixture.SetWatchdogTimestamp("_platformWatchdogTimestamp");
        fixture.SetWatchdogTimestamp("_componentWatchdogTimestamp");
        fixture.SetField("_cumulativeGCPauseDuration", GC.GetTotalPauseDuration() - TimeSpan.FromTicks(1));

        fixture.CheckRuntimeHealth();

        var healthEvent = Assert.Single(fixture.Events);
        Assert.Equal(LocalSiloHealthCheckKind.GarbageCollectionPause, healthEvent.Kind);
        Assert.Equal(0, healthEvent.Score);
        Assert.Contains("garbage collection", healthEvent.Complaint, StringComparison.OrdinalIgnoreCase);
        Assert.True(healthEvent.Duration > TimeSpan.Zero);
        Assert.Null(healthEvent.Source);
    }

    [Fact, TestCategory("BVT")]
    public void CheckRuntimeHealth_RecordsRuntimeStallEvent()
    {
        using var fixture = new WatchdogFixture();
        var stallDuration = TimeSpan.FromSeconds(2) + TimeSpan.FromTicks(1);
        fixture.SetWatchdogTimestamp("_platformWatchdogTimestamp");
        fixture.TimeProvider.Advance(stallDuration);
        fixture.SetWatchdogTimestamp("_componentWatchdogTimestamp");
        fixture.SetField("_cumulativeGCPauseDuration", GC.GetTotalPauseDuration());

        fixture.CheckRuntimeHealth();

        var healthEvent = Assert.Single(
            fixture.Events,
            item => item.Kind == LocalSiloHealthCheckKind.RuntimeStall);
        Assert.Equal(1, healthEvent.Score);
        Assert.Equal(stallDuration, healthEvent.Duration);
        Assert.Contains(".NET Runtime Platform stalled", healthEvent.Complaint, StringComparison.Ordinal);
        Assert.Null(healthEvent.Source);
        Assert.DoesNotContain(
            fixture.Events,
            item => item.Kind == LocalSiloHealthCheckKind.ComponentHealthCheckStall);
    }

    [Fact, TestCategory("BVT")]
    public void CheckRuntimeHealth_DoesNotRecordRuntimeStallAtThreshold()
    {
        using var fixture = new WatchdogFixture();
        fixture.SetWatchdogTimestamp("_platformWatchdogTimestamp");
        fixture.TimeProvider.Advance(TimeSpan.FromSeconds(2));
        fixture.SetWatchdogTimestamp("_componentWatchdogTimestamp");
        fixture.SetField("_cumulativeGCPauseDuration", GC.GetTotalPauseDuration());

        fixture.CheckRuntimeHealth();

        Assert.DoesNotContain(fixture.Events, item => item.Kind == LocalSiloHealthCheckKind.RuntimeStall);
    }

    [Fact, TestCategory("BVT")]
    public void CheckRuntimeHealth_RecordsComponentHealthCheckStallEvent()
    {
        var componentPeriod = TimeSpan.FromSeconds(5);
        using var fixture = new WatchdogFixture(componentPeriod);
        var stallDuration = componentPeriod.Multiply(2) + TimeSpan.FromTicks(1);
        fixture.SetWatchdogTimestamp("_componentWatchdogTimestamp");
        fixture.TimeProvider.Advance(stallDuration);
        fixture.SetWatchdogTimestamp("_platformWatchdogTimestamp");
        fixture.SetField("_cumulativeGCPauseDuration", GC.GetTotalPauseDuration());

        fixture.CheckRuntimeHealth();

        var healthEvent = Assert.Single(
            fixture.Events,
            item => item.Kind == LocalSiloHealthCheckKind.ComponentHealthCheckStall);
        Assert.Equal(1, healthEvent.Score);
        Assert.Equal(stallDuration, healthEvent.Duration);
        Assert.Contains("Participant check thread", healthEvent.Complaint, StringComparison.Ordinal);
        Assert.Null(healthEvent.Source);
        Assert.DoesNotContain(
            fixture.Events,
            item => item.Kind == LocalSiloHealthCheckKind.RuntimeStall);
    }

    [Fact, TestCategory("BVT")]
    public void CheckRuntimeHealth_DoesNotRecordComponentHealthCheckStallAtThreshold()
    {
        var componentPeriod = TimeSpan.FromSeconds(5);
        using var fixture = new WatchdogFixture(componentPeriod);
        fixture.SetWatchdogTimestamp("_componentWatchdogTimestamp");
        fixture.TimeProvider.Advance(componentPeriod.Multiply(2));
        fixture.SetWatchdogTimestamp("_platformWatchdogTimestamp");
        fixture.SetField("_cumulativeGCPauseDuration", GC.GetTotalPauseDuration());

        fixture.CheckRuntimeHealth();

        Assert.DoesNotContain(
            fixture.Events,
            item => item.Kind == LocalSiloHealthCheckKind.ComponentHealthCheckStall);
    }

    private sealed class WatchdogFixture : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly Watchdog _watchdog;
        private readonly RecordingHealthEventRecorder _recorder = new();

        public WatchdogFixture(TimeSpan? componentPeriod = null, IEnumerable<IHealthCheckParticipant>? participants = null)
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            _serviceProvider = services.BuildServiceProvider();
            TimeProvider = new FakeTimeProvider(Start);
            var options = new ClusterMembershipOptions
            {
                LocalHealthDegradationMonitoringPeriod = componentPeriod ?? TimeSpan.FromSeconds(5),
            };
            _watchdog = new Watchdog(
                Options.Create(options),
                participants ?? [],
                NullLogger<Watchdog>.Instance,
                new OrleansInstruments(_serviceProvider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()),
                _recorder,
                TimeProvider);
        }

        public FakeTimeProvider TimeProvider { get; }

        public RecordedHealthEvent[] Events => _recorder.Events;

        public CancellationTokenSource Cancellation => (CancellationTokenSource)typeof(Watchdog)
            .GetField("_cancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(_watchdog)!;

        public void StartWatchdog() => _watchdog.Start();

        public void Stop() => _watchdog.Stop();

        public void SetWatchdogTimestamp(string fieldName)
            => SetField(fieldName, TimeProvider.GetTimestamp());

        public void SetField<T>(string fieldName, T value)
            => typeof(Watchdog)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_watchdog, value);

        public void CheckRuntimeHealth()
            => typeof(Watchdog)
                .GetMethod("CheckRuntimeHealth", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_watchdog, null);

        public void Dispose()
        {
            _watchdog.Dispose();
            _serviceProvider.Dispose();
        }
    }

    private sealed class BlockingHealthParticipant(TaskCompletionSource checking, TaskCompletionSource release) : IHealthCheckParticipant
    {
        public bool CheckHealth(DateTime lastCheckTime, out string reason)
        {
            checking.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            reason = string.Empty;
            return true;
        }
    }

    private sealed class RecordingHealthEventRecorder : ILocalSiloHealthEventRecorder
    {
        private readonly ConcurrentQueue<RecordedHealthEvent> _events = new();

        public RecordedHealthEvent[] Events => _events.ToArray();

        public void RecordHealthEvent(
            LocalSiloHealthCheckKind kind,
            int score,
            string? complaint,
            TimeSpan? duration = null,
            string? source = null)
            => _events.Enqueue(new(kind, score, complaint, duration, source));
    }

    private readonly record struct RecordedHealthEvent(
        LocalSiloHealthCheckKind Kind,
        int Score,
        string? Complaint,
        TimeSpan? Duration,
        string? Source);
}
