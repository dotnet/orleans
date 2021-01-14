using System.Collections.Generic;
using System.Collections.Immutable;

namespace Orleans.Runtime.GrainDirectory
{
    internal interface ILocalClientDirectory
    {
        ImmutableDictionary<GrainId, List<ActivationAddress>> GetRoutingTable();
    }
}
