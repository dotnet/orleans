using System;
using System.Collections.Concurrent;
using System.Threading;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime.Cancellation;

internal class CancellationRuntime : ICancellationRuntime
{
    readonly ConcurrentDictionary<Guid, TokenEntry> _cancellationTokens = new ConcurrentDictionary<Guid, TokenEntry>();

    TokenEntry GetOrCreateEntry(Guid tokenId)
    {
        return _cancellationTokens.GetOrAdd(tokenId, _ => new TokenEntry(new CancellationTokenSource()));
    }

    public void Cancel(Guid tokenId, bool lastCall)
    {
        if (lastCall)
        {
            // On a last call, we can remove the token entry and dispose of it. If no entry exists then we can ignore the call.
            if (_cancellationTokens.TryRemove(tokenId, out var entry))
            {
                entry.CancellationTokenSource.Cancel();
                entry.Dispose();
            }
        }
        else
        {
            // If our invokable has yet to complete, we can cancel the token and leave the entry in place.
            var entry = GetOrCreateEntry(tokenId);
            entry.CancellationTokenSource.Cancel();
        }
    }

    public CancellationToken RegisterCancellableToken(Guid tokenId)
    {
        var entry = GetOrCreateEntry(tokenId);
        return entry.CancellationTokenSource.Token;
    }

    readonly record struct TokenEntry(CancellationTokenSource CancellationTokenSource) : IDisposable
    {
        // TODO: Expire the entry after a certain amount of time

        public void Dispose() => CancellationTokenSource.Dispose();
    }
}