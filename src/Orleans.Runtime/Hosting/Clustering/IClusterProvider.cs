using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Hosting.Clustering
{
    public interface IClusterProvider
    {
        Task<IEnumerable<ExternalClusterMember>> ListMembersAsync(CancellationToken cancellation);

        IAsyncEnumerable<ClusterEvent> MonitorChangesAsync(CancellationToken cancellation);

        string Describe(string name);

        Task DeleteAsync(string name);
    }
}
