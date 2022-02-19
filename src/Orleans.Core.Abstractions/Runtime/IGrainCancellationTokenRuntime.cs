using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    /// <summary>
    /// Functionality required by <see cref="GrainCancellationToken"/> and <see cref="GrainCancellationTokenSource"/>.
    /// </summary>
    internal interface IGrainCancellationTokenRuntime
    {
        /// <summary>
        /// Cancels the <see cref="GrainCancellationToken"/> with the provided id.
        /// </summary>
        /// <param name="id">The grain cancellation token id.</param>
        /// <param name="tokenSource">The grain cancellation token source being canceled.</param>
        /// <param name="grainReferences">The grain references which are observing the cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task Cancel(Guid id, CancellationTokenSource tokenSource, ConcurrentDictionary<GrainId, GrainReference> grainReferences);
    }
}
