using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Runtime.GrainDirectory
{
    internal interface ILocalClientDirectory
    {
        bool TryLocalLookup(GrainId grainId, out List<ActivationAddress> addresses);
        ValueTask<List<ActivationAddress>> Lookup(GrainId grainId);
    }
}
