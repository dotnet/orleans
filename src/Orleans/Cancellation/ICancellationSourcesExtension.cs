using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.Runtime
{
    internal interface ICancellationSourcesExtension : IGrainExtension
    {
        [AlwaysInterleave]
        Task CancelRemoteToken(GrainCancellationToken token);
    }
}