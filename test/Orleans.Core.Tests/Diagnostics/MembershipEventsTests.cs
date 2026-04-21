using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using Orleans.Core.Diagnostics;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.Diagnostics;

public class MembershipEventsTests
{
    [Fact, TestCategory("BVT")]
    public void EmitChanges_EmitsViewChangedOnly()
    {
        using var observer = new Observer(MembershipEvents.AllEvents);
        var observerSilo = CreateSiloAddress(11111, 1);
        var subjectSilo = CreateSiloAddress(11112, 2);
        var snapshot = CreateSnapshot(1, CreateEntry(subjectSilo, SiloStatus.Joining));

        MembershipEvents.EmitViewChanged(snapshot, observerSilo);

        var viewChanged = Assert.Single(observer.Events);
        var typed = Assert.IsType<MembershipEvents.ViewChanged>(viewChanged);
        Assert.Same(snapshot, typed.Snapshot);
        Assert.Same(observerSilo, typed.ObserverSiloAddress);
    }

    [Fact, TestCategory("BVT")]
    public async Task MembershipDiagnosticObserver_DerivesStatusTransitions_FromViewChanged()
    {
        using var observer = MembershipDiagnosticObserver.Create();
        var observerSilo = CreateSiloAddress(12111, 3);
        var subjectSilo = CreateSiloAddress(12112, 4);

        MembershipEvents.EmitViewChanged(
            CreateSnapshot(1, CreateEntry(subjectSilo, SiloStatus.Joining)),
            observerSilo);

        MembershipEvents.EmitViewChanged(
            CreateSnapshot(2, CreateEntry(subjectSilo, SiloStatus.Active)),
            observerSilo);

        var transition = await observer.WaitForSiloBecameActiveAsync(subjectSilo, TimeSpan.FromSeconds(1));

        Assert.NotNull(transition.OldEntry);
        Assert.Equal(SiloStatus.Joining, transition.OldEntry!.Status);
        Assert.Equal(SiloStatus.Active, transition.NewEntry.Status);
        Assert.Same(observerSilo, transition.ObserverSiloAddress);
    }

    private static MembershipEntry CreateEntry(SiloAddress siloAddress, SiloStatus status)
    {
        var now = DateTime.UtcNow;
        return new MembershipEntry
        {
            SiloAddress = siloAddress,
            Status = status,
            StartTime = now,
            IAmAliveTime = now,
            HostName = siloAddress.Endpoint.Address.ToString(),
            SiloName = $"{siloAddress.Endpoint.Port}",
            SuspectTimes = [],
        };
    }

    private static MembershipTableSnapshot CreateSnapshot(long version, params MembershipEntry[] entries)
    {
        return new MembershipTableSnapshot(
            new MembershipVersion(version),
            entries.ToImmutableDictionary(entry => entry.SiloAddress));
    }

    private static SiloAddress CreateSiloAddress(int port, int generation)
    {
        return SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), generation);
    }

    private sealed class Observer : IObserver<MembershipEvents.MembershipEvent>, IDisposable
    {
        private readonly IDisposable _subscription;
        private readonly ConcurrentQueue<MembershipEvents.MembershipEvent> _events = new();

        public Observer(IObservable<MembershipEvents.MembershipEvent> observable)
        {
            _subscription = observable.Subscribe(this);
        }

        public MembershipEvents.MembershipEvent[] Events => _events.ToArray();

        public void Dispose() => _subscription.Dispose();

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(MembershipEvents.MembershipEvent value) => _events.Enqueue(value);
    }
}
