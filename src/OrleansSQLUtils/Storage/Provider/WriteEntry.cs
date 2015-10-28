using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.SqlUtils.StorageProvider
{
    public sealed class WriteEntry
    {
        public GrainIdentity GrainIdentity { get; private set; }
        public IDictionary<string, object> State { get; private set; }
        public TaskCompletionSource<int> CompletionSource { get; private set; }

        internal WriteEntry(GrainIdentity grainIdentity, IDictionary<string, object> state, TaskCompletionSource<int> tcs)
        {
            GrainIdentity = grainIdentity;
            State = state;
            CompletionSource = tcs;
        }
    }
}