using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Hosting.Clustering
{
    /// <summary>
    /// Provides cluster membership information from an external hosting environment.
    /// </summary>
    public interface IClusterProvider
    {
        Task<IEnumerable<ExternalClusterMember>> ListMembersAsync(CancellationToken cancellation);

        IAsyncEnumerable<ClusterEvent> MonitorChangesAsync(CancellationToken cancellation);

        string Describe(string name);

        Task DeleteAsync(string name, CancellationToken cancellation);
    }
}
