
using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Orleans.CodeGeneration;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    internal class MessageFactory
    {
        [ThreadStatic]
        private static ulong _nextId;

        // The nonce reduces the chance of an id collision for a given grain to effectively zero. Id collisions are only relevant in scenarios
        // where where the infinitesimally small chance of a collision is acceptable, such as call cancellation.
        private readonly ulong _seed;
        private readonly DeepCopier _deepCopier;
        private readonly ILogger _logger;
        private readonly MessagingTrace _messagingTrace;

        public MessageFactory(DeepCopier deepCopier, ILogger<MessageFactory> logger, MessagingTrace messagingTrace)
        {
            _deepCopier = deepCopier;
            _logger = logger;
            _messagingTrace = messagingTrace;

            // Generate a 64-bit nonce for the host, to be combined with per-message correlation ids to get a unique, per-host value.
            // This avoids id collisions across different hosts for a given grain.
            _seed = unchecked((ulong)Random.Shared.NextInt64());
        }

        public Message CreateMessage(object body, InvokeMethodOptions options)
        {
            var message = new Message
            {
                Direction = (options & InvokeMethodOptions.OneWay) != 0 ? Message.Directions.OneWay : Message.Directions.Request,
                Id = GetNextCorrelationId(),
                IsReadOnly = (options & InvokeMethodOptions.ReadOnly) != 0,
                IsUnordered = (options & InvokeMethodOptions.Unordered) != 0,
                IsAlwaysInterleave = (options & InvokeMethodOptions.AlwaysInterleave) != 0,
                BodyObject = body,
                RequestContextData = RequestContextExtensions.Export(_deepCopier),
            };

            _messagingTrace.OnCreateMessage(message);
            return message;
        }

        private CorrelationId GetNextCorrelationId()
        {
            // To avoid cross-thread coordination, combine a thread-local counter with the managed thread id. The values are XOR'd together with a
            // 64-bit nonce. Rotating the thread id reduces the chance of collision further by putting the significant bits at the high end, where
            // they are less likely to collide with the per-thread counter, which could become relevant if the counter exceeded 2^32.
            var managedThreadId = Environment.CurrentManagedThreadId;
            var tid = (ulong)(managedThreadId << 16 | managedThreadId >> 16) << 32;
            var id = _seed ^ tid ^ ++_nextId;
            return new CorrelationId(unchecked((long)id));
        }

        public Message CreateResponseMessage(Message request)
        {
            var response = new Message
            {
                IsSystemMessage = request.IsSystemMessage,
                Direction = Message.Directions.Response,
                Id = request.Id,
                IsReadOnly = request.IsReadOnly,
                IsAlwaysInterleave = request.IsAlwaysInterleave,
                TargetSilo = request.SendingSilo,
                TargetGrain = request.SendingGrain,
                SendingSilo = request.TargetSilo,
                SendingGrain = request.TargetGrain,
                CacheInvalidationHeader = request.CacheInvalidationHeader,
                TimeToLive = request.TimeToLive,
                RequestContextData = RequestContextExtensions.Export(_deepCopier),
            };

            _messagingTrace.OnCreateMessage(response);
            return response;
        }

        public Message CreateRejectionResponse(Message request, Message.RejectionTypes type, string info, Exception ex = null)
        {
            var response = CreateResponseMessage(request);
            response.Result = Message.ResponseTypes.Rejection;
            response.BodyObject = new RejectionResponse
            {
                RejectionType = type,
                RejectionInfo = info,
                Exception = ex,
            };
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(
                    ex,
                    "Creating {RejectionType} rejection with info '{Info}' at:" + Environment.NewLine + "{StackTrace}",
                    type,
                    info,
                    Utils.GetStackTrace());
            return response;
        }

        internal Message CreateDiagnosticResponseMessage(Message request, bool isExecuting, bool isWaiting, List<string> diagnostics)
        {
            var response = CreateResponseMessage(request);
            response.Result = Message.ResponseTypes.Status;
            response.BodyObject = new StatusResponse(isExecuting, isWaiting, diagnostics);

            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Creating {RequestMessage} status update with diagnostics {Diagnostics}", request, diagnostics);

            return response;
        }
    }
}