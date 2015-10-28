using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.SqlUtils.StorageProvider
{
    public sealed class ReadEntry 
    {
        public GrainIdentity GrainIdentity { get; private set; }
        public TaskCompletionSource<IDictionary<string, object>> CompletionSource { get; private set; }

        internal ReadEntry(GrainIdentity grainIdentity, TaskCompletionSource<IDictionary<string, object>> tcs)
        {
            GrainIdentity = grainIdentity;
            CompletionSource = tcs;
        }
    }
}