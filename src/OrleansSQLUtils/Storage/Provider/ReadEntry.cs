using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.SqlUtils.StorageProvider
{
    /// <summary>
    /// Entry for a read
    /// </summary>
    internal sealed class ReadEntry 
    {
        /// <summary>
        /// Grain identity
        /// </summary>
        public GrainIdentity GrainIdentity { get; private set; }

        internal TaskCompletionSource<object> CompletionSource { get; private set; }

        internal ReadEntry(GrainIdentity grainIdentity, TaskCompletionSource<object> tcs)
        {
            GrainIdentity = grainIdentity;
            CompletionSource = tcs;
        }
    }
}