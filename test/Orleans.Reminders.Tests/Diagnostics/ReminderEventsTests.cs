using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using Orleans.Runtime;
using Orleans.Testing.Reminders;
using Xunit;
using ReminderEvents = Orleans.Reminders.Diagnostics.ReminderEvents;

namespace UnitTests.Diagnostics;

public class ReminderEventsTests
{
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var tickCompleted = await observer.WaitForReminderTickAsync(grainId, cts.Token, reminderName);

        Assert.Equal(grainId, tickCompleted.GrainId);
        Assert.Equal(reminderName, tickCompleted.ReminderName);
        Assert.Equal(status, tickCompleted.Status);
        Assert.Same(siloAddress, tickCompleted.SiloAddress);
    }

    [Fact, TestCategory("BVT")]
    public async Task ReminderDiagnosticObserver_WaitsForAdditionalTickCount_FromCurrentState()
    {
        using var observer = ReminderDiagnosticObserver.Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var grainId = GrainId.Create("test", "grain");
        const string reminderName = "reminder";
        var now = DateTime.UtcNow;
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 14002), 3);

        ReminderEvents.EmitTickCompleted(
            grainId,
            reminderName,
            new TickStatus(now, TimeSpan.FromSeconds(5), now),
            siloAddress);

        var waitTask = observer.WaitForAdditionalTickCountAsync(grainId, 1, cts.Token, reminderName);
        Assert.False(waitTask.IsCompleted);

        ReminderEvents.EmitTickCompleted(
            grainId,
            reminderName,
            new TickStatus(now, TimeSpan.FromSeconds(5), now.AddSeconds(5)),
            siloAddress);

        await waitTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact, TestCategory("BVT")]
    public async Task ReminderDiagnosticObserver_WaitsForTickCondition_UntilConditionIsSatisfied()
    {
        using var observer = ReminderDiagnosticObserver.Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var grainId = GrainId.Create("test", "grain");
        const string reminderName = "reminder";
        var now = DateTime.UtcNow;
        var period = TimeSpan.FromSeconds(5);
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 14003), 4);
        var acceptedTickCount = 0;

        var waitTask = observer.WaitForTickConditionAsync(
            grainId,
            _ => Task.FromResult(acceptedTickCount >= 1),
            cts.Token,
            reminderName);

        Assert.False(waitTask.IsCompleted);

        ReminderEvents.EmitTickCompleted(
            grainId,
            reminderName,
            new TickStatus(now, period, now),
            siloAddress);

        Assert.False(waitTask.IsCompleted);

        acceptedTickCount = 1;
        ReminderEvents.EmitTickCompleted(
            grainId,
            reminderName,
            new TickStatus(now, period, now.Add(period)),
            siloAddress);

        await waitTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

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

    [Fact, TestCategory("BVT")]
    public async Task ReminderDiagnosticObserver_WaitsForReminderQuiescence_FromLifecycleEvents()
    {
        using var observer = ReminderDiagnosticObserver.Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var grainId = GrainId.Create("test", "grain");
        const string reminderName = "reminder";
        var identity1 = new object();
        var identity2 = new object();
        var siloAddress1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 14005), 6);
        var siloAddress2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 14006), 7);

        ReminderEvents.EmitLocalReminderStarted(grainId, reminderName, identity1, siloAddress1);
        ReminderEvents.EmitLocalReminderStarted(grainId, reminderName, identity2, siloAddress2);
        Assert.Equal(2, observer.GetActiveReminderCount(grainId, reminderName));

        var waitTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cts.Token);
        Assert.False(waitTask.IsCompleted);

        ReminderEvents.EmitLocalReminderStopped(grainId, reminderName, identity1, ReminderEvents.LocalReminderStopReason.RemovedFromTable, siloAddress1);
        Assert.False(waitTask.IsCompleted);
        Assert.Equal(1, observer.GetActiveReminderCount(grainId, reminderName));

        ReminderEvents.EmitLocalReminderStopped(grainId, reminderName, identity2, ReminderEvents.LocalReminderStopReason.Unregistered, siloAddress2);

        await waitTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));
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
