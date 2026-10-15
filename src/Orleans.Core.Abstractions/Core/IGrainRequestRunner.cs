using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime;

/// <summary>
/// A grain context which runs callbacks as requests on its activation.
/// </summary>
internal interface IGrainRequestRunner
{
    /// <summary>
    /// Queues <paramref name="callback"/> as a request on this activation.
    /// </summary>
    /// <remarks>
    /// The returned task completes with the result of the <see cref="Task{TResult}"/> returned by the callback, or with
    /// <see langword="default"/> when the callback returns a non-generic <see cref="Task"/>.
    /// </remarks>
    Task<TResult> RunRequestAsync<TState, TResult>(
        Func<TState, CancellationToken, Task> callback,
        TState state,
        in GrainRequestOptions options,
        CancellationToken cancellationToken);
}
