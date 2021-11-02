using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Runtime.GrainDirectory
{
    internal interface ILocalClientDirectory
    {
        bool TryLocalLookup(GrainId grainId, out List<GrainAddress> addresses);
        ValueTask<List<GrainAddress>> Lookup(GrainId grainId);
    }
}
