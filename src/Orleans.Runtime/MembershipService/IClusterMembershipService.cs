using System.Threading.Tasks;
using Orleans.Runtime.Utilities;

namespace Orleans.Runtime
{
    internal interface IClusterMembershipService
    {
        ClusterMembershipSnapshot CurrentSnapshot { get; }

        IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates { get; }

        ValueTask Refresh(MembershipVersion minimumVersion = default);
    }
}
