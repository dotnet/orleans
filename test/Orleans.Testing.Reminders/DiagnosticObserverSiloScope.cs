using Orleans.Runtime;
using Orleans.TestingHost;

namespace Orleans.Testing.Reminders;

internal sealed class DiagnosticObserverSiloScope
{
    private readonly Func<SiloAddress, bool> _containsSilo;

    private DiagnosticObserverSiloScope(Func<SiloAddress, bool> containsSilo)
    {
        _containsSilo = containsSilo;
    }

    public static DiagnosticObserverSiloScope For(InProcessTestCluster cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        return new(cluster.ContainsSilo);
    }

    public static DiagnosticObserverSiloScope For(InProcessTestClusterBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return new(builder.ContainsSilo);
    }

    public static DiagnosticObserverSiloScope For(TestCluster cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        return new(cluster.ContainsSilo);
    }

    public static DiagnosticObserverSiloScope For(TestClusterBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return new(builder.ContainsSilo);
    }

    public static DiagnosticObserverSiloScope For(SiloAddress siloAddress)
    {
        ArgumentNullException.ThrowIfNull(siloAddress);
        return new(candidate => siloAddress.Endpoint.Equals(candidate.Endpoint));
    }

    public static DiagnosticObserverSiloScope All { get; } = new(static _ => true);

    public bool Matches(SiloAddress? siloAddress) => siloAddress is not null && _containsSilo(siloAddress);
}
