using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Orleans.Serialization.Buffers;

// Codec-acquired owners share release state with rollback, including when a custom codec releases them early.
internal sealed class ArcBufferLease(ArcBuffer buffer)
{
    private readonly ArcBuffer _buffer = buffer;
    private int _pins = 1;

    internal void CheckValidity()
    {
        if (Volatile.Read(ref _pins) <= 0)
        {
            throw new InvalidOperationException("The Arc buffer ownership has been released.");
        }
    }

    internal void Pin()
    {
        _buffer.Pin();
        Interlocked.Increment(ref _pins);
    }

    internal void Unpin()
    {
        int pins;
        do
        {
            pins = Volatile.Read(ref _pins);
            if (pins <= 0)
            {
                throw new InvalidOperationException("The Arc buffer ownership has been released.");
            }
        }
        while (Interlocked.CompareExchange(ref _pins, pins - 1, pins) != pins);

        var buffer = _buffer;
        buffer.Unpin();
    }

    internal void ReleaseAll()
    {
        var pins = Interlocked.Exchange(ref _pins, 0);
        for (var i = 0; i < pins; i++)
        {
            var buffer = _buffer;
            buffer.Unpin();
        }
    }
}

// Only active returned-value operations enlist ownership; direct codec calls remain caller-owned.
internal struct ArcBufferLeaseTracker
{
    private List<ArcBufferLease>? _leases;
    private int _depth;

    internal int Begin()
    {
        _depth++;
        return _leases?.Count ?? 0;
    }

    internal ArcBuffer Track(ArcBuffer buffer)
    {
        if (_depth == 0 || buffer.First is null) return buffer;

        var tracked = false;
        try
        {
            var lease = new ArcBufferLease(buffer);
            (_leases ??= new()).Add(lease);
            var result = new ArcBuffer(buffer, lease);
            tracked = true;
            return result;
        }
        finally
        {
            if (!tracked) buffer.Dispose();
        }
    }

    internal void End(int position, bool success)
    {
        Debug.Assert(_depth > 0);
        _depth--;
        if (!success && _leases is { } leases)
        {
            for (var i = leases.Count - 1; i >= position; i--)
            {
                leases[i].ReleaseAll();
            }

            leases.RemoveRange(position, leases.Count - position);
        }

        // A nested success remains provisional until the enclosing result is returned.
        if (_depth == 0) _leases = null;
    }
}
