using System;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Async
{
    internal interface ICancellationSourcesExtension : IGrainExtension, IGrain
    {
        [AlwaysInterleave]
        Task CancelTokenSource(GrainCancellationToken token);
    }
}