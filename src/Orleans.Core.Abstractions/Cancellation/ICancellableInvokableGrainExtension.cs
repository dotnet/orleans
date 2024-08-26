using System;
using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.Runtime;

public interface ICancellableInvokableGrainExtension : IGrainExtension
{
    /// <summary>
    /// Indicates that a cancellation token has been canceled.
    /// </summary>
    /// <param name="tokenId">
    /// The token id</param>
    /// <returns>
    /// A <see cref="Task"/> representing the operation.
    /// </returns>
    [AlwaysInterleave]
    Task CancelRemoteToken(Guid tokenId);
}