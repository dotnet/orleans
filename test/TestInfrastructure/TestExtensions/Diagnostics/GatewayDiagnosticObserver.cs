#nullable enable
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Orleans.Core.Diagnostics;
using Orleans.Runtime;

namespace TestExtensions;

public sealed class GatewayDiagnosticObserver : IDisposable
{
    private readonly IConnectableObservable<GatewayEvents.GatewayEvent> _events;
    private readonly IDisposable _connection;

    public static GatewayDiagnosticObserver Create() => new();

    private GatewayDiagnosticObserver()
    {
        _events = GatewayEvents.AllEvents.Replay();
        _connection = _events.Connect();
    }

    public async Task WaitForClientDroppedAsync(SiloAddress siloAddress, GrainId clientId, CancellationToken cancellationToken)
    {
        await _events
            .OfType<GatewayEvents.ClientDropped>()
            .FirstAsync(e => e.SiloAddress.Equals(siloAddress) && e.ClientId == clientId)
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task WaitForClientsDroppedAsync(IEnumerable<(SiloAddress SiloAddress, GrainId ClientId)> clients, CancellationToken cancellationToken)
    {
        var tasks = clients
            .Distinct()
            .Select(client => WaitForClientDroppedAsync(client.SiloAddress, client.ClientId, cancellationToken));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public void Dispose() => _connection.Dispose();
}
