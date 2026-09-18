using System;
using System.Collections.Generic;
using System.Threading;

namespace Orleans.Runtime.Messaging
{
    // ClientState serializes access so that registration and the transport enqueue are atomic with removal.
    internal sealed class GatewayInFlightRequestTracker(
        TimeProvider timeProvider,
        TimeSpan responseTimeout,
        int maxForwardCount)
    {
        private static long _nextAttempt;
        private readonly int _maxDeferredResponses = Math.Max(1, maxForwardCount + 1);
        private Dictionary<CorrelationId, TrackedRequest>? _requests;
        // Updates can cross different silo connections, so later forwarding hops can arrive before earlier ones.
        private Dictionary<CorrelationId, List<ForwardingUpdate>>? _forwardingUpdates;
        private Dictionary<CorrelationId, List<Message>>? _deferredResponses;

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

            _requests ??= [];
            if (request.GatewayRequestAttempt == 0)
            {
                request.GatewayRequestAttempt = Interlocked.Increment(ref _nextAttempt);
            }
            else if (!_requests.TryGetValue(request.Id, out var current)
                || current.Attempt != request.GatewayRequestAttempt)
            {
                return false;
            }

            var trackedRequest = new TrackedRequest(
                request.Id,
                request.GatewayRequestAttempt,
                request.IsSystemMessage,
                request.IsReadOnly,
                request.IsAlwaysInterleave,
                request.SendingSilo,
                request.SendingGrain,
                targetSilo,
                request.TargetGrain,
                request.ForwardCount,
                request.CacheInvalidationHeader is { } cacheInvalidationHeader ? new(cacheInvalidationHeader) : null,
                timeProvider.GetTimestamp(),
                explicitTimeToLive.HasValue,
                retentionPeriod);

            _requests[request.Id] = trackedRequest;
            _forwardingUpdates?.Remove(request.Id);
            _deferredResponses?.Remove(request.Id);
            return true;
        }

        internal CompletionResult TryComplete(Message response)
        {
            if (response.Direction != Message.Directions.Response || response.Result == Message.ResponseTypes.Status)
            {
                return CompletionResult.NotTracked;
            }

            if (_requests is not { } requests || !requests.TryGetValue(response.Id, out var trackedRequest))
            {
                return response.GatewayRequestAttempt == 0
                    ? CompletionResult.NotTracked
                    : CompletionResult.Superseded;
            }

            if (response.GatewayRequestAttempt != 0 && response.GatewayRequestAttempt != trackedRequest.Attempt)
            {
                return CompletionResult.Superseded;
            }

            if (response.GatewayRequestAttempt != 0 && response.ForwardCount < trackedRequest.ForwardCount)
            {
                return CompletionResult.Superseded;
            }

            if (response.ForwardCount > trackedRequest.ForwardCount)
            {
                requests.Remove(response.Id);
                ClearAuxiliaryState(response.Id);
                return CompletionResult.Completed;
            }

            if (response.SendingSilo is not { } responseSilo)
            {
                return CompletionResult.Deferred;
            }

            if (!responseSilo.Equals(trackedRequest.TargetSilo))
            {
                _deferredResponses ??= [];
                if (!_deferredResponses.TryGetValue(response.Id, out var responses))
                {
                    _deferredResponses[response.Id] = responses = [];
                }

                responses.RemoveAll(item => item.SendingSilo?.Equals(responseSilo) is true);
                if (responses.Count >= _maxDeferredResponses)
                {
                    responses.RemoveAt(0);
                }

                responses.Add(response);
                return CompletionResult.Deferred;
            }

            requests.Remove(response.Id);
            ClearAuxiliaryState(response.Id);
            return CompletionResult.Completed;
        }

        internal ForwardingUpdateResult TryUpdateDestination(
            CorrelationId requestId,
            SiloAddress sourceSilo,
            SiloAddress targetSilo,
            int forwardCount,
            long attempt,
            out SiloAddress updatedTargetSilo,
            out Message? completedResponse)
        {
            updatedTargetSilo = null!;
            completedResponse = null;
            if (_requests is not { } requests || !requests.TryGetValue(requestId, out var trackedRequest))
            {
                return ForwardingUpdateResult.Ignored;
            }

            if (attempt != 0 && attempt != trackedRequest.Attempt)
            {
                return ForwardingUpdateResult.Ignored;
            }

            if (forwardCount <= trackedRequest.ForwardCount)
            {
                return ForwardingUpdateResult.Ignored;
            }

            _forwardingUpdates ??= [];
            if (!_forwardingUpdates.TryGetValue(requestId, out var updates))
            {
                _forwardingUpdates[requestId] = updates = [];
            }

            updates.RemoveAll(update => update.ForwardCount == forwardCount);
            updates.Add(new(sourceSilo, targetSilo, forwardCount));

            var updated = false;
            // Apply only the contiguous forwarding chain rooted at the destination which this tracker owns.
            while (updates.FindIndex(
                update => update.ForwardCount == trackedRequest.ForwardCount + 1
                    && update.SourceSilo.Equals(trackedRequest.TargetSilo)) is var index
                && index >= 0)
            {
                var update = updates[index];
                updates.RemoveAt(index);
                trackedRequest = trackedRequest with
                {
                    TargetSilo = update.TargetSilo,
                    ForwardCount = update.ForwardCount,
                };
                requests[requestId] = trackedRequest;
                updated = true;
            }

            if (updates.Count == 0)
            {
                _forwardingUpdates.Remove(requestId);
            }

            updatedTargetSilo = trackedRequest.TargetSilo;
            var currentTargetSilo = updatedTargetSilo;
            if (updated
                && _deferredResponses is { } deferredResponses
                && deferredResponses.TryGetValue(requestId, out var responses)
                && responses.Find(
                    response => response.SendingSilo?.Equals(currentTargetSilo) is true
                        && (response.GatewayRequestAttempt == 0
                            || response.ForwardCount == trackedRequest.ForwardCount)) is { } response)
            {
                requests.Remove(requestId);
                ClearAuxiliaryState(requestId);
                completedResponse = response;
            }

            return updated ? ForwardingUpdateResult.Applied : ForwardingUpdateResult.Recorded;
        }

        internal bool TryRemove(CorrelationId requestId, out Message request)
        {
            if (_requests?.Remove(requestId, out var trackedRequest) is true)
            {
                ClearAuxiliaryState(requestId);
                request = CreateRequest(trackedRequest);
                return true;
            }

            request = null!;
            return false;
        }

        internal bool TryClaimForRejection(Message request, SiloAddress targetSilo, out Message requestToReject)
        {
            if (_requests is not { } requests || !requests.TryGetValue(request.Id, out var trackedRequest))
            {
                if (request.GatewayRequestAttempt != 0)
                {
                    requestToReject = null!;
                    return false;
                }

                requestToReject = request;
                return true;
            }

            if (request.GatewayRequestAttempt != 0 && request.GatewayRequestAttempt != trackedRequest.Attempt)
            {
                requestToReject = null!;
                return false;
            }

            if (targetSilo.Equals(trackedRequest.TargetSilo))
            {
                requests.Remove(request.Id);
                ClearAuxiliaryState(request.Id);
                requestToReject = CreateRequest(trackedRequest);
                return true;
            }

            // Forwarding advances ForwardCount, so this request supersedes the destination captured by the prior send.
            if (request.ForwardCount > trackedRequest.ForwardCount)
            {
                requests.Remove(request.Id);
                ClearAuxiliaryState(request.Id);
                requestToReject = request;
                return true;
            }

            requestToReject = null!;
            return false;
        }

        internal bool Contains(CorrelationId requestId) => _requests?.ContainsKey(requestId) is true;

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
                    ClearAuxiliaryState(id);
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
                    ClearAuxiliaryState(id);
                }
            }
        }

        internal void Clear()
        {
            _requests?.Clear();
            _forwardingUpdates?.Clear();
            _deferredResponses?.Clear();
        }

        private void ClearAuxiliaryState(CorrelationId requestId)
        {
            _forwardingUpdates?.Remove(requestId);
            _deferredResponses?.Remove(requestId);
        }

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
                GatewayRequestAttempt = request.Attempt,
                IsSystemMessage = request.IsSystemMessage,
                IsReadOnly = request.IsReadOnly,
                IsAlwaysInterleave = request.IsAlwaysInterleave,
                SendingSilo = request.SendingSilo,
                SendingGrain = request.SendingGrain,
                TargetSilo = request.TargetSilo,
                TargetGrain = request.TargetGrain,
                ForwardCount = request.ForwardCount,
                CacheInvalidationHeader = request.CacheInvalidationHeader,
                TimeToLive = timeToLive,
            };
        }

        private readonly record struct TrackedRequest(
            CorrelationId Id,
            long Attempt,
            bool IsSystemMessage,
            bool IsReadOnly,
            bool IsAlwaysInterleave,
            SiloAddress? SendingSilo,
            GrainId SendingGrain,
            SiloAddress TargetSilo,
            GrainId TargetGrain,
            int ForwardCount,
            List<GrainAddressCacheUpdate>? CacheInvalidationHeader,
            long StartTimestamp,
            bool HasTimeToLive,
            TimeSpan RetentionPeriod);

        private readonly record struct ForwardingUpdate(
            SiloAddress SourceSilo,
            SiloAddress TargetSilo,
            int ForwardCount);

        internal enum CompletionResult
        {
            NotTracked,
            Completed,
            Deferred,
            Superseded,
        }

        internal enum ForwardingUpdateResult
        {
            Ignored,
            Recorded,
            Applied,
        }
    }
}
