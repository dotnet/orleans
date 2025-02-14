using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime.Cancellation;

internal class CancellableInvokableGrainExtension : ICancellableInvokableGrainExtension, IDisposable
{
    readonly ICancellationRuntime _runtime;
    readonly Timer _cleanupTimer;

    public CancellableInvokableGrainExtension(IGrainContext grainContext)
    {
        _runtime = grainContext.GetComponent<ICancellationRuntime>();
        _cleanupTimer = new Timer(static obj => ((CancellableInvokableGrainExtension)obj)._runtime.ExpireTokens(), this, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public Task CancelRemoteToken(Guid tokenId)
    {
        if (_runtime is not null)
        {
            _runtime.Cancel(tokenId, lastCall: false);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }
}
