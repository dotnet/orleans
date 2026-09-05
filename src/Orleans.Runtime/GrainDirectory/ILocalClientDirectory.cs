using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Orleans.Runtime.GrainDirectory
{
    internal interface ILocalClientDirectory
    {
        bool TryLocalLookup(GrainId grainId, [NotNullWhen(true)] out List<GrainAddress>? addresses);
        ValueTask<List<GrainAddress>> Lookup(GrainId grainId);
        void InvalidateCache(GrainId grainId);
    }
}
