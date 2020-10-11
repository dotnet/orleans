using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    public interface IClusterMembershipService
    {
        ClusterMembershipSnapshot CurrentSnapshot { get; }

        IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates { get; }

        ValueTask Refresh(MembershipVersion minimumVersion = default);

        /// <summary>
        /// Unilaterally declares the specified silo defunct.
        /// </summary>
        Task<bool> TryKill(SiloAddress siloAddress);
    }
}
