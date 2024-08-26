using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.Cancellation;

internal class CancellableInvokableGrainExtension : ICancellableInvokableGrainExtension
{
    public Task CancelRemoteToken(Guid tokenId)
    {
        throw new NotImplementedException();
    }
}
