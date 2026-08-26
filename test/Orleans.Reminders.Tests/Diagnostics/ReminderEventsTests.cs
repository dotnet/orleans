using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using Orleans.Runtime;
using Orleans.Testing.Reminders;
using TestExtensions;
using Xunit;
using ReminderEvents = Orleans.Reminders.Diagnostics.ReminderEvents;

namespace UnitTests.Diagnostics;

public class ReminderEventsTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void EmitRegistered_EmitsGrainIdAndReminderName()
    {
        using var observer = new Observer(ReminderEvents.AllEvents);
        var grainId = GrainId.Create("test", "grain");
        var reminderName = "reminder";
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 14000), 1);

        ReminderEvents.EmitRegistered(grainId, reminderName, siloAddress);

        var registered = Assert.Single(
            observer.Events.OfType<ReminderEvents.Registered>(),
            evt => evt.GrainId == grainId && evt.ReminderName == reminderName);
        Assert.Same(siloAddress, registered.SiloAddress);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task ReminderDiagnosticObserver_MatchesTickCompleted_ByIdentifiers()
    {
        using var observer = ReminderDiagnosticObserver.Create();
        var grainId = GrainId.Create("test", "grain");
        const string reminderName = "reminder";
        var now = DateTime.UtcNow;
        var status = new TickStatus(now, TimeSpan.FromSeconds(5), now);
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 14001), 2);

        ReminderEvents.EmitTickCompleted(grainId, reminderName, status, siloAddress);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(1));
        var tickCompleted = await observer.WaitForReminderTickAsync(grainId, cts.Token, reminderName);

        Assert.Equal(grainId, tickCompleted.GrainId);
        Assert.Equal(reminderName, tickCompleted.ReminderName);
        Assert.Equal(status, tickCompleted.Status);
        Assert.Same(siloAddress, tickCompleted.SiloAddress);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task ReminderDiagnosticObserver_WaitsForAdditionalTickCount_FromCurrentState()
    {
        using var observer = ReminderDiagnosticObserver.Create();
        var grainId = GrainId.Create("test", "grain");
        const string reminderName = "reminder";
        var now = DateTime.UtcNow;
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 14002), 3);

        ReminderEvents.EmitTickCompleted(
            grainId,
            reminderName,
            new TickStatus(now, TimeSpan.FromSeconds(5), now),
            siloAddress);

        var waitTask = observer.WaitForAdditionalTickCountAsync(grainId, 1, TestContext.Current.CancellationToken, reminderName);
        Assert.False(waitTask.IsCompleted);

        ReminderEvents.EmitTickCompleted(
            grainId,
            reminderName,
            new TickStatus(now, TimeSpan.FromSeconds(5), now.AddSeconds(5)),
            siloAddress);

        Assert.True(waitTask.IsCompletedSuccessfully);
        await waitTask;
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task ReminderDiagnosticObserver_WaitsForTickCondition_UntilConditionIsSatisfied()
    {
        using var observer = ReminderDiagnosticObserver.Create();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        var grainId = GrainId.Create("test", "grain");
        const string reminderName = "reminder";
        var now = DateTime.UtcNow;
        var period = TimeSpan.FromSeconds(5);
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 14003), 4);
        var secondConditionCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var conditionCheckCount = 0;

        var waitTask = observer.WaitForTickConditionAsync(
            grainId,
            _ =>
            {
                var currentCheck = Interlocked.Increment(ref conditionCheckCount);
                if (currentCheck == 2)
                {
                    secondConditionCheck.TrySetResult();
                }

                return Task.FromResult(currentCheck >= 3);
            },
            cancellation.Token,
            reminderName);

        Assert.False(waitTask.IsCompleted);
        Assert.Equal(1, conditionCheckCount);

        ReminderEvents.EmitTickCompleted(
            grainId,
            reminderName,
            new TickStatus(now, period, now),
            siloAddress);

        await secondConditionCheck.Task.WaitAsync(cancellation.Token);
        Assert.False(waitTask.IsCompleted);

        ReminderEvents.EmitTickCompleted(
            grainId,
            reminderName,
            new TickStatus(now, period, now.Add(period)),
            siloAddress);

        await waitTask;
        Assert.Equal(3, conditionCheckCount);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void EmitLocalReminderLifecycle_EmitsReminderInstanceAndReason()
    {
        using var observer = new Observer(ReminderEvents.AllEvents);
        var grainId = GrainId.Create("test", "grain");
        const string reminderName = "reminder";
        var identity = new object();
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 14004), 5);

        ReminderEvents.EmitLocalReminderStarted(grainId, reminderName, identity, siloAddress);
        ReminderEvents.EmitLocalReminderStopped(grainId, reminderName, identity, ReminderEvents.LocalReminderStopReason.Unregistered, siloAddress);

        var started = Assert.Single(
            observer.Events.OfType<ReminderEvents.LocalReminderStarted>(),
            evt => evt.GrainId == grainId && evt.ReminderName == reminderName);
        Assert.Same(identity, started.Identity);
        Assert.Same(siloAddress, started.SiloAddress);

        var stopped = Assert.Single(
            observer.Events.OfType<ReminderEvents.LocalReminderStopped>(),
            evt => evt.GrainId == grainId && evt.ReminderName == reminderName);
        Assert.Same(identity, stopped.Identity);
        Assert.Equal(ReminderEvents.LocalReminderStopReason.Unregistered, stopped.Reason);
        Assert.Same(siloAddress, stopped.SiloAddress);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task ReminderDiagnosticObserver_WaitsForReminderQuiescence_FromLifecycleEvents()
    {
        using var observer = ReminderDiagnosticObserver.Create();
        var grainId = GrainId.Create("test", "grain");
        const string reminderName = "reminder";
        var identity1 = new object();
        var identity2 = new object();
        var siloAddress1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 14005), 6);
        var siloAddress2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 14006), 7);

        ReminderEvents.EmitLocalReminderStarted(grainId, reminderName, identity1, siloAddress1);
        ReminderEvents.EmitLocalReminderStarted(grainId, reminderName, identity2, siloAddress2);
        Assert.Equal(2, observer.GetActiveReminderCount(grainId, reminderName));

        var waitTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, TestContext.Current.CancellationToken);
        Assert.False(waitTask.IsCompleted);

        ReminderEvents.EmitLocalReminderStopped(grainId, reminderName, identity1, ReminderEvents.LocalReminderStopReason.RemovedFromTable, siloAddress1);
        Assert.False(waitTask.IsCompleted);
        Assert.Equal(1, observer.GetActiveReminderCount(grainId, reminderName));

        ReminderEvents.EmitLocalReminderStopped(grainId, reminderName, identity2, ReminderEvents.LocalReminderStopReason.Unregistered, siloAddress2);

        Assert.True(waitTask.IsCompletedSuccessfully);
        await waitTask;
        Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task ReminderDiagnosticObserver_CanceledQuiescenceWait_RemainsCanceled()
    {
        using var observer = ReminderDiagnosticObserver.Create();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var grainId = GrainId.Create("test", "grain");
        const string reminderName = "reminder";
        var identity = new object();
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 14007), 8);

        ReminderEvents.EmitLocalReminderStarted(grainId, reminderName, identity, siloAddress);
        var canceledWaitTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cts.Token);

        cts.Cancel();

        Assert.True(canceledWaitTask.IsCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaitTask);

        var currentWaitTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, TestContext.Current.CancellationToken);
        ReminderEvents.EmitLocalReminderStopped(
            grainId,
            reminderName,
            identity,
            ReminderEvents.LocalReminderStopReason.Unregistered,
            siloAddress);

        Assert.True(currentWaitTask.IsCompletedSuccessfully);
        await currentWaitTask;
        Assert.True(canceledWaitTask.IsCanceled);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Reminders")]
    [Fact, TestCategory("BVT")]
    public async Task ReminderDiagnosticObserver_WaitsForCurrentOwnerSchedule_AfterOwnershipChange()
    {
        using var observer = ReminderDiagnosticObserver.Create();
        var grainId = GrainId.Create("test", "grain");
        const string reminderName = "reminder";
        var previousOwner = new object();
        var currentOwner = new object();
        var previousSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 14008), 9);
        var currentSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 14009), 10);
        var now = DateTime.UtcNow;

        ReminderEvents.EmitLocalReminderStarted(grainId, reminderName, previousOwner, previousSilo);
        ReminderEvents.EmitLocalReminderScheduleChanged(grainId, reminderName, previousOwner, 2, previousSilo);
        ReminderEvents.EmitLocalReminderTickWaitArmed(grainId, reminderName, previousOwner, 2, previousSilo);
        ReminderEvents.EmitLocalReminderStarted(grainId, reminderName, currentOwner, currentSilo);
        ReminderEvents.EmitLocalReminderTickWaitArmed(grainId, reminderName, currentOwner, 0, currentSilo);
        ReminderEvents.EmitTickFiring(
            grainId,
            reminderName,
            new TickStatus(now, TimeSpan.FromSeconds(5), now),
            previousSilo);

        var waitTask = observer.WaitForLocalReminderScheduleAsync(grainId, reminderName, TestContext.Current.CancellationToken);
        Assert.False(waitTask.IsCompleted);

        ReminderEvents.EmitLocalReminderStopped(
            grainId,
            reminderName,
            previousOwner,
            ReminderEvents.LocalReminderStopReason.RemovedFromRange,
            previousSilo);

        Assert.True(waitTask.IsCompletedSuccessfully);
        await waitTask;
    }

    private sealed class Observer : IObserver<ReminderEvents.ReminderEvent>, IDisposable
    {
        private readonly IDisposable _subscription;
        private readonly ConcurrentQueue<ReminderEvents.ReminderEvent> _events = new();

        public Observer(IObservable<ReminderEvents.ReminderEvent> observable)
        {
            _subscription = observable.Subscribe(this);
        }

        public ReminderEvents.ReminderEvent[] Events => _events.ToArray();

        public void Dispose() => _subscription.Dispose();

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(ReminderEvents.ReminderEvent value) => _events.Enqueue(value);
    }
}
