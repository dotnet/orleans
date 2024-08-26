using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime.Cancellation;

internal class CancellationRuntime : ICancellationRuntime
{
    private static readonly TimeSpan _cleanupFrequency = TimeSpan.FromMinutes(7);

    readonly Dictionary<Guid, TokenEntry> _cancellationTokens = new Dictionary<Guid, TokenEntry>();

    ref TokenEntry GetOrCreateEntry(Guid tokenId)
    {
        lock (_cancellationTokens)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_cancellationTokens, tokenId, out var exists);

            if (!exists)
            {
                entry.SetSource(new CancellationTokenSource());
            }

            entry.Touch();
            return ref entry;
        }
    }

    public void Cancel(Guid tokenId, bool lastCall)
    {
        if (!lastCall)
        {
            var entry = GetOrCreateEntry(tokenId);
            entry.Source.Cancel();
        }
        else
        {
            lock (_cancellationTokens)
            {
                if (_cancellationTokens.Remove(tokenId, out var entry))
                {
                    entry.Source.Cancel();
                    entry.Source.Dispose();
                }
            }
        }
    }

    public CancellationToken RegisterCancellableToken(Guid tokenId, CancellationToken @default)
    {
        var entry = GetOrCreateEntry(tokenId);

        if (@default != CancellationToken.None)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(@default, entry.Source.Token).Token;
        }

        return entry.Source.Token;
    }

    public void ExpireTokens()
    {
        var now = Stopwatch.GetTimestamp();
        lock (_cancellationTokens)
        {
            foreach (var token in _cancellationTokens)
            {
                if (token.Value.IsExpired(_cleanupFrequency, now))
                {
                    _cancellationTokens.Remove(token.Key);
                }
            }
        }
    }

    struct TokenEntry 
    {
        private long _createdTime;

        public void Touch() => _createdTime = Stopwatch.GetTimestamp();

        public void SetSource(CancellationTokenSource source)
        {
            Source = source;
        }

        public CancellationTokenSource Source { get; private set; }

        public bool IsExpired(TimeSpan expiry, long nowTimestamp)
        {
            var untouchedTime = TimeSpan.FromSeconds((nowTimestamp - _createdTime) / (double)Stopwatch.Frequency);

            return untouchedTime >= expiry;
        }
    }
}