using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    interface IGrainCancellationTokenRuntime
    {
        Task Cancel(Guid id, CancellationTokenSource tokenSource, ConcurrentDictionary<GrainId, GrainReference> grainReferences);
    }
}
