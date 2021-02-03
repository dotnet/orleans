
using System;
using System.Collections.Generic;
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
        private readonly MessagingTrace messagingTrace;

        public MessageFactory(SerializationManager serializationManager, ILogger<MessageFactory> logger, MessagingTrace messagingTrace)
        {
            this.serializationManager = serializationManager;
            this.logger = logger;
            this.messagingTrace = messagingTrace;
        }

        public Message CreateMessage(InvokeMethodRequest request, InvokeMethodOptions options)
        {
            var message = new Message
            {
                Category = Message.Categories.Application,
                Direction = (options & InvokeMethodOptions.OneWay) != 0 ? Message.Directions.OneWay : Message.Directions.Request,
                Id = CorrelationId.GetNext(),
                IsReadOnly = (options & InvokeMethodOptions.ReadOnly) != 0,
                IsUnordered = (options & InvokeMethodOptions.Unordered) != 0,
                IsAlwaysInterleave = (options & InvokeMethodOptions.AlwaysInterleave) != 0,
                BodyObject = request,
                RequestContextData = RequestContextExtensions.Export(this.serializationManager)
            };

            if (options.IsTransactional())
            {
                SetTransaction(message, options);
            }
            else
            {
                // clear transaction info if not in transaction
                message.RequestContextData?.Remove(TransactionContext.Orleans_TransactionContext_Key);
            }

            messagingTrace.OnCreateMessage(message);
            return message;
        }

        private void SetTransaction(Message message, InvokeMethodOptions options)
        { 
            // clear transaction info if transaction operation requires new transaction.
            ITransactionInfo transactionInfo = TransactionContext.GetTransactionInfo();

            // enforce join transaction calls
            if(options.IsTransactionOption(InvokeMethodOptions.TransactionJoin) && transactionInfo == null)
            {
                throw new NotSupportedException("Call cannot be made outside of a transaction.");
            }

            // enforce not allowed transaction calls
            if (options.IsTransactionOption(InvokeMethodOptions.TransactionNotAllowed) && transactionInfo != null)
            {
                throw new NotSupportedException("Call cannot be made within a transaction.");
            }

            // clear transaction context if creating a transaction or transaction is suppressed
            if (options.IsTransactionOption(InvokeMethodOptions.TransactionCreate) ||
                options.IsTransactionOption(InvokeMethodOptions.TransactionSuppress))
            {
                transactionInfo = null;
            }

            bool isTransactionRequired = options.IsTransactionOption(InvokeMethodOptions.TransactionCreate) ||
                                         options.IsTransactionOption(InvokeMethodOptions.TransactionCreateOrJoin) ||
                                         options.IsTransactionOption(InvokeMethodOptions.TransactionJoin);

            message.TransactionInfo = transactionInfo?.Fork();
            message.IsTransactionRequired = isTransactionRequired;
            if (transactionInfo == null)
            {
                // if we're leaving a transaction context, make sure it's been cleared from the request context.
                message.RequestContextData?.Remove(TransactionContext.Orleans_TransactionContext_Key);
            }
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
                TraceContext = request.TraceContext,
                TransactionInfo = request.TransactionInfo
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

            var contextData = RequestContextExtensions.Export(this.serializationManager);
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