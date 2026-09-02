using System;
using System.Collections.Generic;

namespace Orleans.Runtime.Messaging
{
    // ClientState serializes access so that registration and the transport enqueue are atomic with removal.
    internal sealed class GatewayInFlightRequestTracker(TimeProvider timeProvider, TimeSpan responseTimeout)
    {
        private Dictionary<CorrelationId, TrackedRequest>? _requests;

        internal int Count => _requests?.Count ?? 0;

        internal bool Track(Message request)
        {
            if (request.Direction != Message.Directions.Request
                || request.TargetSilo is not { } targetSilo
                || request.TargetGrain.IsSystemTarget())
            {
                return false;
            }

            var explicitTimeToLive = request.TimeToLive;
            var retentionPeriod = explicitTimeToLive ?? responseTimeout;
            if (retentionPeriod <= TimeSpan.Zero)
            {
                return false;
            }

            var trackedRequest = new TrackedRequest(
                request.Id,
                request.IsSystemMessage,
                request.IsReadOnly,
                request.IsAlwaysInterleave,
                request.SendingSilo,
                request.SendingGrain,
                targetSilo,
                request.TargetGrain,
                request.CacheInvalidationHeader is { } cacheInvalidationHeader ? new(cacheInvalidationHeader) : null,
                timeProvider.GetTimestamp(),
                explicitTimeToLive.HasValue,
                retentionPeriod);

            _requests ??= [];
            _requests[request.Id] = trackedRequest;
            return true;
        }

        internal bool TryComplete(Message response)
        {
            if (response.Direction != Message.Directions.Response || response.Result == Message.ResponseTypes.Status)
            {
                return false;
            }

            return _requests?.Remove(response.Id) is true;
        }

        internal bool TryRemove(CorrelationId requestId, out Message request)
        {
            if (_requests?.Remove(requestId, out var trackedRequest) is true)
            {
                request = CreateRequest(trackedRequest);
                return true;
            }

            request = null!;
            return false;
        }

        internal bool TryRemove(CorrelationId requestId, SiloAddress targetSilo, out Message request)
        {
            if (_requests is { } requests
                && requests.TryGetValue(requestId, out var trackedRequest)
                && targetSilo.Equals(trackedRequest.TargetSilo)
                && requests.Remove(requestId))
            {
                request = CreateRequest(trackedRequest);
                return true;
            }

            request = null!;
            return false;
        }

        internal List<Message>? RemoveForSilo(SiloAddress silo)
        {
            if (_requests is not { Count: > 0 } requests)
            {
                return null;
            }

            List<CorrelationId>? ids = null;
            List<Message>? result = null;
            foreach (var (id, request) in requests)
            {
                if (silo.Equals(request.TargetSilo))
                {
                    ids ??= [];
                    result ??= [];
                    ids.Add(id);
                    result.Add(CreateRequest(request));
                }
            }

            if (ids is not null)
            {
                foreach (var id in ids)
                {
                    requests.Remove(id);
                }
            }

            return result;
        }

        internal void RemoveExpired()
        {
            if (_requests is not { Count: > 0 } requests)
            {
                return;
            }

            List<CorrelationId>? expired = null;
            foreach (var (id, request) in requests)
            {
                if (timeProvider.GetElapsedTime(request.StartTimestamp) >= request.RetentionPeriod)
                {
                    expired ??= [];
                    expired.Add(id);
                }
            }

            if (expired is not null)
            {
                foreach (var id in expired)
                {
                    requests.Remove(id);
                }
            }
        }

        internal void Clear() => _requests?.Clear();

        private Message CreateRequest(TrackedRequest request)
        {
            TimeSpan? timeToLive = null;
            if (request.HasTimeToLive)
            {
                timeToLive = request.RetentionPeriod - timeProvider.GetElapsedTime(request.StartTimestamp);
                if (timeToLive < TimeSpan.Zero)
                {
                    timeToLive = TimeSpan.Zero;
                }
            }

            return new Message
            {
                Direction = Message.Directions.Request,
                Id = request.Id,
                IsSystemMessage = request.IsSystemMessage,
                IsReadOnly = request.IsReadOnly,
                IsAlwaysInterleave = request.IsAlwaysInterleave,
                SendingSilo = request.SendingSilo,
                SendingGrain = request.SendingGrain,
                TargetSilo = request.TargetSilo,
                TargetGrain = request.TargetGrain,
                CacheInvalidationHeader = request.CacheInvalidationHeader,
                TimeToLive = timeToLive,
            };
        }

        private readonly record struct TrackedRequest(
            CorrelationId Id,
            bool IsSystemMessage,
            bool IsReadOnly,
            bool IsAlwaysInterleave,
            SiloAddress? SendingSilo,
            GrainId SendingGrain,
            SiloAddress TargetSilo,
            GrainId TargetGrain,
            List<GrainAddressCacheUpdate>? CacheInvalidationHeader,
            long StartTimestamp,
            bool HasTimeToLive,
            TimeSpan RetentionPeriod);
    }
}
