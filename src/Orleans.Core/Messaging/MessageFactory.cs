
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Orleans.CodeGeneration;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    internal class MessageFactory
    {
        private readonly DeepCopier deepCopier;
        private readonly ILogger logger;
        private readonly MessagingTrace messagingTrace;

        public MessageFactory(DeepCopier deepCopier, ILogger<MessageFactory> logger, MessagingTrace messagingTrace)
        {
            this.deepCopier = deepCopier;
            this.logger = logger;
            this.messagingTrace = messagingTrace;
        }

        public Message CreateMessage(object body, InvokeMethodOptions options)
        {
            var message = new Message
            {
                Category = Message.Categories.Application,
                Direction = (options & InvokeMethodOptions.OneWay) != 0 ? Message.Directions.OneWay : Message.Directions.Request,
                Id = CorrelationId.GetNext(),
                IsReadOnly = (options & InvokeMethodOptions.ReadOnly) != 0,
                IsUnordered = (options & InvokeMethodOptions.Unordered) != 0,
                IsAlwaysInterleave = (options & InvokeMethodOptions.AlwaysInterleave) != 0,
                BodyObject = body,
                RequestContextData = RequestContextExtensions.Export(this.deepCopier)
            };

            messagingTrace.OnCreateMessage(message);
            return message;
        }

        public Message CreateResponseMessage(Message request)
        {
            var response = new Message
            {
                Category = request.Category,
                Direction = Message.Directions.Response,
                Id = request.Id,
                IsReadOnly = request.IsReadOnly,
                IsAlwaysInterleave = request.IsAlwaysInterleave,
                TargetSilo = request.SendingSilo,
            };

            if (!request.SendingGrain.IsDefault)
            {
                response.TargetGrain = request.SendingGrain;
                if (request.SendingActivation != null)
                {
                    response.TargetActivation = request.SendingActivation;
                }
            }

            response.SendingSilo = request.TargetSilo;
            if (!request.TargetGrain.IsDefault)
            {
                response.SendingGrain = request.TargetGrain;
                if (request.TargetActivation != null)
                {
                    response.SendingActivation = request.TargetActivation;
                }
                else if (request.TargetGrain.IsSystemTarget())
                {
                    response.SendingActivation = ActivationId.GetDeterministic(request.TargetGrain);
                }
            }

            response.CacheInvalidationHeader = request.CacheInvalidationHeader;
            response.TimeToLive = request.TimeToLive;

            var contextData = RequestContextExtensions.Export(this.deepCopier);
            if (contextData != null)
            {
                response.RequestContextData = contextData;
            }

            messagingTrace.OnCreateMessage(response);
            return response;
        }

        public Message CreateRejectionResponse(Message request, Message.RejectionTypes type, string info, Exception ex = null)
        {
            var response = this.CreateResponseMessage(request);
            response.Result = Message.ResponseTypes.Rejection;
            response.RejectionType = type;
            response.RejectionInfo = info;
            response.BodyObject = ex;
            if (this.logger.IsEnabled(LogLevel.Debug)) this.logger.Debug("Creating {0} rejection with info '{1}' for {2} at:" + Environment.NewLine + "{3}", type, info, this, Utils.GetStackTrace());
            return response;
        }

        internal Message CreateDiagnosticResponseMessage(Message request, bool isExecuting, bool isWaiting, List<string> diagnostics)
        {
            var response = this.CreateResponseMessage(request);
            response.Result = Message.ResponseTypes.Status;
            response.BodyObject = new StatusResponse(isExecuting, isWaiting, diagnostics);

            if (this.logger.IsEnabled(LogLevel.Debug)) this.logger.LogDebug("Creating {RequestMesssage} status update with diagnostics {Diagnostics}", request, diagnostics);

            return response;
        }
    }
}