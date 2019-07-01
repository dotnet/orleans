using System.Collections.Generic;
using Orleans.Runtime.Utilities;

namespace Orleans.Runtime
{
    internal interface IClusterMembershipService
    {
        ClusterMembershipSnapshot CurrentSnapshot { get; }

        IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates { get; }
    }
}
