using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Threading
{
    // Grain that serves as cancellation token source holder
    internal interface ICancellationTokenHolderGrain : IGrainWithGuidKey
    {
        Task<CancellationToken> GetCancellationToken();
        Task Cancel(TimeSpan deactivationDelay);
        Task Dispose();
    }
}
