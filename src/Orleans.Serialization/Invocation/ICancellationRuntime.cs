using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Serialization.Invocation;

/// <summary>
/// An optional runtime to fascilitate in cancelling invokables 
/// </summary>
public interface ICancellationRuntime
{
    /// <summary>
    /// Registers the token and returns a cancellation token linked to the token id
    /// </summary>
    /// <param name="tokenId">The token id to register</param>
    /// <returns>A cancellationToken that will be cancelled once Cancel for the token has been called</returns>
    CancellationToken RegisterCancellableToken(Guid tokenId);

    /// <summary>
    /// Cancels the invokable with the specified token id
    /// </summary>
    /// <param name="tokenId">The token id to cancel</param>
    /// <param name="lastCall">Whether this is the last call associated with the token</param>
    void Cancel(Guid tokenId, bool lastCall);
}