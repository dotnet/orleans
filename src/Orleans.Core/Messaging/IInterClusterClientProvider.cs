using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime;

/// <summary>
/// Provides connected Orleans clients for remote clusters.
/// </summary>
public interface IInterClusterClientProvider
{
    /// <summary>
    /// Gets a connected client for the provided cluster.
    /// </summary>
    ValueTask<IClusterClient> GetClient(
        ClusterIdentity destination,
        CancellationToken cancellationToken = default);
}
