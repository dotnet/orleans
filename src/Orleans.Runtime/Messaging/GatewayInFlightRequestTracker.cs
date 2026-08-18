using System;
using System.Collections.Generic;

namespace Orleans.Runtime.Messaging
{
    internal sealed class GatewayInFlightRequestTracker<TClient>(TimeProvider timeProvider, TimeSpan responseTimeout)
        where TClient : class
    {
        private readonly object _lock = new();
        private readonly Dictionary<TClient, Dictionary<CorrelationId, TrackedRequest>> _requests = new(ReferenceEqualityComparer.Instance);
        private int _count;

        internal int ActiveClientCount
        {
            get
            {
                lock (_lock)
                {
                    return _requests.Count;
                }
            }
        }

        internal int Count
        {
            get
            {
                lock (_lock)
                {
                    return _count;
                }
            }
        }

        internal bool Track(TClient client, Message request)
        {
            if (request.Direction != Message.Directions.Request
                || request.TargetSilo is not { } targetSilo
                || request.TargetGrain.IsSystemTarget())
            {
                return false;
            }

            var timeToLive = request.TimeToLive ?? responseTimeout;
            if (timeToLive <= TimeSpan.Zero)
            {
                return false;
            }

            var snapshot = new Message
            {
                Direction = Message.Directions.Request,
                Id = request.Id,
                IsSystemMessage = request.IsSystemMessage,
                IsReadOnly = request.IsReadOnly,
                IsAlwaysInterleave = request.IsAlwaysInterleave,
                SendingSilo = request.SendingSilo,
                SendingGrain = request.SendingGrain,
                TargetSilo = targetSilo,
                TargetGrain = request.TargetGrain,
                CacheInvalidationHeader = request.CacheInvalidationHeader is { } cacheInvalidationHeader
                    ? new(cacheInvalidationHeader)
                    : null,
                TimeToLive = timeToLive,
            };

            lock (_lock)
            {
                if (!_requests.TryGetValue(client, out var clientRequests))
                {
                    clientRequests = [];
                    _requests.Add(client, clientRequests);
                }

                var trackedRequest = new TrackedRequest(snapshot, timeProvider.GetTimestamp(), timeToLive);
                if (clientRequests.TryAdd(request.Id, trackedRequest))
                {
                    _count++;
                }
                else
                {
                    clientRequests[request.Id] = trackedRequest;
                }
            }

            return true;
        }

        internal bool TryComplete(TClient client, Message response)
        {
            if (response.Direction != Message.Directions.Response || response.Result == Message.ResponseTypes.Status)
            {
                return false;
            }

            lock (_lock)
            {
                return TryRemoveCore(client, response.Id, out _);
            }
        }

        internal bool TryRemove(TClient client, CorrelationId requestId, out Message request)
        {
            lock (_lock)
            {
                return TryRemoveCore(client, requestId, out request);
            }
        }

        internal List<(TClient Client, Message Request)>? RemoveForSilo(SiloAddress silo)
        {
            lock (_lock)
            {
                List<(TClient Client, CorrelationId RequestId, Message Request)>? matches = null;
                foreach (var (client, clientRequests) in _requests)
                {
                    foreach (var (requestId, request) in clientRequests)
                    {
                        if (silo.Equals(request.Message.TargetSilo))
                        {
                            matches ??= [];
                            matches.Add((client, requestId, request.Message));
                        }
                    }
                }

                if (matches is null)
                {
                    return null;
                }

                var result = new List<(TClient Client, Message Request)>(matches.Count);
                foreach (var (client, requestId, request) in matches)
                {
                    if (TryRemoveCore(client, requestId, out _))
                    {
                        result.Add((client, request));
                    }
                }

                return result;
            }
        }

        internal void RemoveExpired()
        {
            lock (_lock)
            {
                List<(TClient Client, CorrelationId RequestId)>? expired = null;
                foreach (var (client, clientRequests) in _requests)
                {
                    foreach (var (requestId, request) in clientRequests)
                    {
                        if (timeProvider.GetElapsedTime(request.StartTimestamp) >= request.TimeToLive)
                        {
                            expired ??= [];
                            expired.Add((client, requestId));
                        }
                    }
                }

                if (expired is not null)
                {
                    foreach (var (client, requestId) in expired)
                    {
                        TryRemoveCore(client, requestId, out _);
                    }
                }
            }
        }

        internal void Clear(TClient client)
        {
            lock (_lock)
            {
                if (_requests.Remove(client, out var requests))
                {
                    _count -= requests.Count;
                }
            }
        }

        internal void Clear()
        {
            lock (_lock)
            {
                _requests.Clear();
                _count = 0;
            }
        }

        private bool TryRemoveCore(TClient client, CorrelationId requestId, out Message request)
        {
            if (_requests.TryGetValue(client, out var clientRequests)
                && clientRequests.Remove(requestId, out var trackedRequest))
            {
                _count--;
                if (clientRequests.Count == 0)
                {
                    _requests.Remove(client);
                }

                request = trackedRequest.Message;
                return true;
            }

            request = null!;
            return false;
        }

        private readonly record struct TrackedRequest(Message Message, long StartTimestamp, TimeSpan TimeToLive);
    }
}
