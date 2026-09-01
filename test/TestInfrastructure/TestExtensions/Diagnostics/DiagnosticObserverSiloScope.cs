using Orleans.Runtime;
using Orleans.TestingHost;

namespace TestExtensions;

internal sealed class DiagnosticObserverSiloScope
{
    private readonly Func<SiloAddress, bool> _containsSilo;
    private readonly string? _clusterId;

    private DiagnosticObserverSiloScope(
        Func<SiloAddress, bool> containsSilo,
        string? clusterId = null,
        bool includesAllSilos = false)
    {
        _containsSilo = containsSilo;
        _clusterId = clusterId;
        IncludesAllSilos = includesAllSilos;
    }

    public static DiagnosticObserverSiloScope For(InProcessTestCluster cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        return new(cluster.ContainsSilo, cluster.Options.ClusterId);
    }

    public static DiagnosticObserverSiloScope For(InProcessTestClusterBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return new(builder.ContainsSilo, builder.Options.ClusterId);
    }

    public static DiagnosticObserverSiloScope For(TestCluster cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        return new(cluster.ContainsSilo, cluster.Options.ClusterId);
    }

    public static DiagnosticObserverSiloScope For(SiloAddress siloAddress)
    {
        ArgumentNullException.ThrowIfNull(siloAddress);
        return new(candidate => HasSameEndpoint(siloAddress, candidate));
    }

    public static DiagnosticObserverSiloScope All { get; } = new(static _ => true, includesAllSilos: true);

    public bool IncludesAllSilos { get; }

    public bool Matches(SiloAddress? siloAddress, string? clusterId = null) =>
        IncludesAllSilos
        || (siloAddress is not null
            ? _containsSilo(siloAddress)
            : clusterId is not null && clusterId == _clusterId);

    private static bool HasSameEndpoint(SiloAddress left, SiloAddress right) => left.Endpoint.Equals(right.Endpoint);
}
