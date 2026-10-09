using System;
using System.Threading;

namespace Orleans.DurableMessaging;

/// <summary>
/// Coalesces physical durable-job callbacks which represent the same logical pump ownership.
/// </summary>
internal sealed class DurableMessagingPumpCoordinator
{
    private readonly object _lock = new();
    private string? _activeOwnershipId;
    private CancellationToken _activeCancellationToken;
    private long _activeGeneration;

    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _activeOwnershipId is not null;
            }
        }
    }

    public bool TryAcquire(
        string ownershipId,
        CancellationToken cancellationToken,
        out DurableMessagingPumpLease lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownershipId);

        lock (_lock)
        {
            if (string.Equals(_activeOwnershipId, ownershipId, StringComparison.Ordinal)
                && !_activeCancellationToken.IsCancellationRequested)
            {
                lease = default;
                return false;
            }

            lease = new DurableMessagingPumpLease(ownershipId, ++_activeGeneration);
            _activeOwnershipId = ownershipId;
            _activeCancellationToken = cancellationToken;
            return true;
        }
    }

    public bool IsCurrent(DurableMessagingPumpLease lease)
    {
        lock (_lock)
        {
            return _activeGeneration == lease.Generation
                && string.Equals(_activeOwnershipId, lease.OwnershipId, StringComparison.Ordinal);
        }
    }

    public void Release(DurableMessagingPumpLease lease)
    {
        lock (_lock)
        {
            if (_activeGeneration == lease.Generation
                && string.Equals(_activeOwnershipId, lease.OwnershipId, StringComparison.Ordinal))
            {
                _activeOwnershipId = null;
                _activeCancellationToken = default;
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _activeOwnershipId = null;
            _activeCancellationToken = default;
        }
    }
}

internal readonly record struct DurableMessagingPumpLease(string OwnershipId, long Generation);
