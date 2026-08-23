using System.Diagnostics;
using Microsoft.Extensions.ObjectPool;

namespace Orleans.Runtime;

/// <summary>
/// Owns the waiting and running request collections for one activation while it has tracked requests.
/// The owning <see cref="ActivationData"/> lock protects all request access. A tracker is returned to
/// the pool only after both collections become empty, so pooled state is never observable by another activation.
/// </summary>
internal sealed class ActivationRequestTracker
{
    private const int MaxPooledCollectionCapacity = 64;
    private static readonly ObjectPool<ActivationRequestTracker> Pool = ObjectPool.Create(new PoolPolicy());
    private List<(Message Message, CoarseStopwatch QueuedTime)>? _waitingRequests;
    private Dictionary<Message, CoarseStopwatch>? _runningRequests;
    private bool _isRented;

    internal int WaitingCount => _waitingRequests?.Count ?? 0;
    internal int RunningCount => _runningRequests?.Count ?? 0;
    internal int Count => WaitingCount + RunningCount;
    internal bool IsEmpty => Count == 0;
    internal List<(Message Message, CoarseStopwatch QueuedTime)>? WaitingRequests => _waitingRequests;
    internal Dictionary<Message, CoarseStopwatch>? RunningRequests => _runningRequests;

    internal static ActivationRequestTracker Rent()
    {
        var result = Pool.Get();
        result.OnRent();
        return result;
    }

    internal void Return()
    {
        OnReturn();
        Pool.Return(this);
    }

    internal void AddWaiting(Message message)
    {
        Debug.Assert(_isRented);
        (_waitingRequests ??= new(capacity: 1)).Add((message, CoarseStopwatch.StartNew()));
    }

    internal void RemoveWaitingAt(int index)
    {
        Debug.Assert(_isRented);
        _waitingRequests!.RemoveAt(index);
    }

    internal void AddRunning(Message message, CoarseStopwatch stopwatch)
    {
        Debug.Assert(_isRented);
        (_runningRequests ??= new(capacity: 1)).Add(message, stopwatch);
    }

    internal bool RemoveRunning(Message message)
    {
        Debug.Assert(_isRented);
        return _runningRequests?.Remove(message) ?? false;
    }

    internal List<Message> DequeueAllWaitingRequests()
    {
        Debug.Assert(_isRented);
        if (_waitingRequests is not { Count: > 0 } waitingRequests)
        {
            return [];
        }

        var result = new List<Message>(waitingRequests.Count);
        foreach (var (message, _) in waitingRequests)
        {
            // Local-only messages are not allowed to escape the activation.
            if (!message.IsLocalOnly)
            {
                result.Add(message);
            }
        }

        waitingRequests.Clear();
        return result;
    }

    internal bool TryFindRunningRequest(GrainId senderGrainId, CorrelationId messageId, out Message? message)
    {
        Debug.Assert(_isRented);
        if (_runningRequests is { Count: > 0 } runningRequests)
        {
            foreach (var candidate in runningRequests.Keys)
            {
                if (candidate.Id == messageId && candidate.SendingGrain == senderGrainId)
                {
                    message = candidate;
                    return true;
                }
            }
        }

        message = null;
        return false;
    }

    internal bool TryRemoveWaitingRequest(GrainId senderGrainId, CorrelationId messageId, out Message? message)
    {
        Debug.Assert(_isRented);
        if (_waitingRequests is { Count: > 0 } waitingRequests)
        {
            for (var i = 0; i < waitingRequests.Count; i++)
            {
                var candidate = waitingRequests[i].Message;
                if (candidate.Id == messageId && candidate.SendingGrain == senderGrainId)
                {
                    waitingRequests.RemoveAt(i);
                    message = candidate;
                    return true;
                }
            }
        }

        message = null;
        return false;
    }

    internal void OnRent()
    {
        if (_isRented)
        {
            throw new InvalidOperationException("An activation request tracker cannot be leased more than once.");
        }

        _isRented = true;
        Debug.Assert(IsEmpty);
    }

    internal void OnReturn()
    {
        if (!_isRented)
        {
            throw new InvalidOperationException("An activation request tracker cannot be returned more than once.");
        }

        if (!IsEmpty)
        {
            throw new InvalidOperationException("An activation request tracker can only be returned when it is empty.");
        }

        _isRented = false;
        _waitingRequests?.Clear();
        _runningRequests?.Clear();
    }

    private sealed class PoolPolicy : PooledObjectPolicy<ActivationRequestTracker>
    {
        public override ActivationRequestTracker Create() => new();

        public override bool Return(ActivationRequestTracker obj)
            => (obj._waitingRequests?.Capacity ?? 0) <= MaxPooledCollectionCapacity
                && (obj._runningRequests?.EnsureCapacity(0) ?? 0) <= MaxPooledCollectionCapacity;
    }
}
