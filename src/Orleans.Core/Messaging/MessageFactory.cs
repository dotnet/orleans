
using System;
using Microsoft.Extensions.Logging;
using Orleans.CodeGeneration;
using Orleans.Serialization;
using Orleans.Transactions;

namespace Orleans.Runtime
{
    internal class MessageFactory
    {
        private readonly SerializationManager serializationManager;
        private readonly ILogger logger;

        public MessageFactory(SerializationManager serializationManager, ILogger<MessageFactory> logger)
        {
            this.serializationManager = serializationManager;
            this.logger = logger;
        }

        public Message CreateMessage(InvokeMethodRequest request, InvokeMethodOptions options)
        {
            var direction = (options & InvokeMethodOptions.OneWay) != 0 ? Message.Directions.OneWay : Message.Directions.Request;
            var message = new Message
            {
                Category = Message.Categories.Application,
                Direction = direction,
                Id = CorrelationId.GetNext(),
                IsReadOnly = (options & InvokeMethodOptions.ReadOnly) != 0,
                IsUnordered = (options & InvokeMethodOptions.Unordered) != 0,
                IsTransactionRequired = (options & InvokeMethodOptions.TransactionRequiresNew) != 0 || (options & InvokeMethodOptions.TransactionRequired) != 0,
                BodyObject = request,
                IsUsingInterfaceVersions = request.InterfaceVersion > 0,
            };

            ITransactionInfo transactionInfo = message.IsTransactionRequired ? TransactionContext.GetTransactionInfo() : null;
            if (transactionInfo != null)
            {
                message.TransactionInfo = transactionInfo.Fork();
            }

            if ((options & InvokeMethodOptions.AlwaysInterleave) != 0)
                message.IsAlwaysInterleave = true;

            message.RequestContextData = RequestContextExtensions.Export(this.serializationManager);
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
                TransactionInfo = request.TransactionInfo
            };

            if (request.SendingGrain != null)
            {
                response.TargetGrain = request.SendingGrain;
                if (request.SendingActivation != null)
                {
                    response.TargetActivation = request.SendingActivation;
                }
            }

            response.SendingSilo = request.TargetSilo;
            if (request.TargetGrain != null)
            {
                response.SendingGrain = request.TargetGrain;
                if (request.TargetActivation != null)
                {
                    response.SendingActivation = request.TargetActivation;
                }
                else if (request.TargetGrain.IsSystemTarget)
                {
                    response.SendingActivation = ActivationId.GetSystemActivation(request.TargetGrain, request.TargetSilo);
                }
            }

            if (request.DebugContext != null)
            {
                response.DebugContext = request.DebugContext;
            }

            response.CacheInvalidationHeader = request.CacheInvalidationHeader;
            response.TimeToLive = request.TimeToLive;

            var contextData = RequestContextExtensions.Export(this.serializationManager);
            if (contextData != null)
            {
                response.RequestContextData = contextData;
            }

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
    }
}