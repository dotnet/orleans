using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using Orleans;
using Orleans.Runtime;
using TestExtensions;
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

        ReminderEvents.EmitTickCompleted(grainId, reminderName, status, siloAddress, new RemindableStub());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var tickCompleted = await observer.WaitForReminderTickAsync(grainId, reminderName, cts.Token);

        Assert.Equal(grainId, tickCompleted.GrainId);
        Assert.Equal(reminderName, tickCompleted.ReminderName);
        Assert.Equal(status, tickCompleted.Status);
        Assert.Same(siloAddress, tickCompleted.SiloAddress);
    }

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
            siloAddress,
            new RemindableStub());

        var waitTask = observer.WaitForAdditionalTickCountAsync(grainId, 1, reminderName);
        Assert.False(waitTask.IsCompleted);

        ReminderEvents.EmitTickCompleted(
            grainId,
            reminderName,
            new TickStatus(now, TimeSpan.FromSeconds(5), now.AddSeconds(5)),
            siloAddress,
            new RemindableStub());

        await waitTask.WaitAsync(TimeSpan.FromSeconds(1));
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

    private sealed class RemindableStub : IRemindable
    {
        public Task ReceiveReminder(string reminderName, TickStatus status) => Task.CompletedTask;
    }
}
