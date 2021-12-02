using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime.Hosting.Clustering;

namespace Orleans.Hosting.Kubernetes
{
    public interface IClusterProvider
    {
        Task<IEnumerable<ExternalClusterMember>> ListMembersAsync(CancellationToken cancellation);

        IAsyncEnumerable<ClusterEvent> MonitorChangesAsync(CancellationToken cancellation);

        string Describe(string name);

        Task DeleteAsync(string name);
    }
}
