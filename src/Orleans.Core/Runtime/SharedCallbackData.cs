using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    internal class SharedCallbackData
    {
        private readonly SerializationManager serializationManager;
        public readonly Func<Message, bool> ShouldResend;
        public readonly Action<Message> Unregister;
        public readonly ILogger Logger;
        public readonly MessagingOptions MessagingOptions;
        public long ResponseTimeoutStopwatchTicks;

        public SharedCallbackData(
            Func<Message, bool> resendFunc,
            Action<Message> unregister,
            ILogger logger,
            MessagingOptions messagingOptions,
            SerializationManager serializationManager,
            ApplicationRequestsStatisticsGroup requestStatistics)
        {
            RequestStatistics = requestStatistics;
            this.ShouldResend = resendFunc;
            this.Unregister = unregister;
            this.serializationManager = serializationManager;
            this.Logger = logger;
            this.MessagingOptions = messagingOptions;
            this.ResponseTimeout = messagingOptions.ResponseTimeout;
        }

        public ApplicationRequestsStatisticsGroup RequestStatistics { get; }

        public TimeSpan ResponseTimeout
        {
            get => this.MessagingOptions.ResponseTimeout;
            set
            {
                this.MessagingOptions.ResponseTimeout = value;
                this.ResponseTimeoutStopwatchTicks = (long)(value.TotalSeconds * Stopwatch.Frequency);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public void ResponseCallback(Message message, TaskCompletionSource<object> context)
        {
            Response response;
            if (message.Result != Message.ResponseTypes.Rejection)
            {
                try
                {
                    response = (Response)message.GetDeserializedBody(this.serializationManager);
                }
                catch (Exception exc)
                {
                    //  catch the Deserialize exception and break the promise with it.
                    response = Response.ExceptionResponse(exc);
                }
            }
            else
            {
                Exception rejection;
                switch (message.RejectionType)
                {
                    case Message.RejectionTypes.GatewayTooBusy:
                        rejection = new GatewayTooBusyException();
                        break;
                    case Message.RejectionTypes.DuplicateRequest:
                        return; // Ignore duplicates

                    default:
                        rejection = message.GetDeserializedBody(this.serializationManager) as Exception;
                        if (rejection == null)
                        {
                            if (string.IsNullOrEmpty(message.RejectionInfo))
                            {
                                message.RejectionInfo = "Unable to send request - no rejection info available";
                            }
                            rejection = new OrleansMessageRejectionException(message.RejectionInfo);
                        }
                        break;
                }
                response = Response.ExceptionResponse(rejection);
            }

            if (!response.ExceptionFlag)
            {
                context.TrySetResult(response.Data);
            }
            else
            {
                context.TrySetException(response.Exception);
            }
        }
    }
}