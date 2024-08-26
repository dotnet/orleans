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

    CancellationTokenSource _reusableCancellationTokenSource;

    ref TokenEntry GetOrCreateEntry(Guid tokenId)
    {
        lock (_cancellationTokens)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_cancellationTokens, tokenId, out var exists);

            if (!exists)
            {
                var cancellationTokenSource = _reusableCancellationTokenSource;
                if (cancellationTokenSource is not null)
                {
                    _reusableCancellationTokenSource = null;
                }
                else
                {
                    cancellationTokenSource = new CancellationTokenSource();
                }
                entry.SetSource(cancellationTokenSource);
            }

            entry.Touch();
            return ref entry;
        }
    }

    public void Cancel(Guid tokenId, bool lastCall)
    {
        var entry = GetOrCreateEntry(tokenId);
        entry.Source.Cancel();

        if (lastCall)
        {
            // Cancel the source on the last call
            entry.Source.Cancel();

            // Try and reuse the source
            if (_reusableCancellationTokenSource is not null || entry.Source.TryReset() is false || Interlocked.CompareExchange(ref _reusableCancellationTokenSource, entry.Source, null) != entry.Source)
            {
                // Dispose if we failed to reuse
                entry.Source.Dispose();
            }

            lock (_cancellationTokens)
            {
                _cancellationTokens.Remove(tokenId);
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