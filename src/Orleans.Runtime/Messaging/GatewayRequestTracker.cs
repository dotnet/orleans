using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime.Messaging;

internal sealed class GatewayRequestTracker(TimeProvider timeProvider, TimeSpan defaultResponseTimeout)
{
    private readonly ConcurrentDictionary<(GrainId GrainId, CorrelationId CorrelationId), TrackedRequest> _requests = new();

    internal int Count => _requests.Count;

    internal IReadOnlyCollection<(GrainId GrainId, CorrelationId CorrelationId)> Keys => [.. _requests.Keys];

    internal void Register(Message request)
    {
        var timeout = request.TimeToLive
            ?? request.GetGatewayRequestTimeout()
            ?? (request.BodyObject as IInvokable)?.GetDefaultResponseTimeout()
            ?? defaultResponseTimeout;
        var deadline = timeProvider.GetTimestamp() + checked((long)Math.Ceiling(timeout.TotalSeconds * timeProvider.TimestampFrequency));
        _requests[(request.SendingGrain, request.Id)] = new(request, deadline);
    }

    internal bool Complete(Message response)
        => _requests.TryRemove((response.TargetGrain, response.Id), out _);

    internal bool Complete(GrainId sourceId, CorrelationId correlationId)
        => _requests.TryRemove((sourceId, correlationId), out _);

    internal bool Remove(Message request)
        => _requests.TryRemove((request.SendingGrain, request.Id), out _);

    internal void RemoveExpired()
    {
        var now = timeProvider.GetTimestamp();
        foreach (var (key, request) in _requests)
        {
            if (now >= request.Deadline)
            {
                _requests.TryRemove(key, out _);
            }
        }
    }

    internal IEnumerable<Message> Drain()
    {
        foreach (var (key, request) in _requests)
        {
            if (_requests.TryRemove(key, out _))
            {
                yield return request.Message;
            }
        }
    }

    private readonly record struct TrackedRequest(Message Message, long Deadline);
}
