using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.SqlUtils.StorageProvider
{
    /// <summary>
    /// Entry for a write
    /// </summary>
    public sealed class WriteEntry
    {
        /// <summary>
        /// Grain identity
        /// </summary>
        public GrainIdentity GrainIdentity { get; private set; }

        /// <summary>
        /// Grain state to write
        /// </summary>
        public IDictionary<string, object> State { get; private set; }

        internal TaskCompletionSource<int> CompletionSource { get; private set; }

        internal WriteEntry(GrainIdentity grainIdentity, IDictionary<string, object> state, TaskCompletionSource<int> tcs)
        {
            GrainIdentity = grainIdentity;
            State = state;
            CompletionSource = tcs;
        }
    }
}