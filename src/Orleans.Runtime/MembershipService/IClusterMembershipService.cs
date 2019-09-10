using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal interface IClusterMembershipService
    {
        ClusterMembershipSnapshot CurrentSnapshot { get; }

        IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates { get; }

        ValueTask Refresh(MembershipVersion minimumVersion = default);
    }
}
