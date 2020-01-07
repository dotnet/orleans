using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal sealed class RuntimeMessagingTrace : MessagingTrace
    {
        public const string DispatcherReceiveInvalidActivationEventName = Category + ".Dispatcher.InvalidActivation";
        public const string DispatcherDetectedDeadlockEventName = Category + ".Dispatcher.DetectedDeadlock";
        public const string DispatcherDiscardedRejectionEventName = Category + ".Dispatcher.DiscardedRejection";
        public const string DispatcherRejectedMessageEventName = Category + ".Dispatcher.Rejected";
        public const string DispatcherForwardingEventName = Category + ".Dispatcher.Forwarding";
        public const string DispatcherForwardingMultipleEventName = Category + ".Dispatcher.ForwardingMultiple";
        public const string DispatcherForwardingFailedEventName = Category + ".Dispatcher.ForwardingFailed";

        private static readonly Action<ILogger, ActivationState, Message, Exception> LogDispatcherReceiveInvalidActivation =
            LoggerMessage.Define<ActivationState, Message>(
                LogLevel.Warning,
                new EventId((int)ErrorCode.Dispatcher_Receive_InvalidActivation, DispatcherReceiveInvalidActivationEventName),
                "Response received for {State} activation {Message}");

        private static readonly Action<ILogger, Message, ActivationData, Exception> LogDispatcherDetectedDeadlock =
            LoggerMessage.Define<Message, ActivationData>(
                LogLevel.Warning,
                new EventId((int)ErrorCode.Dispatcher_DetectedDeadlock, DispatcherDetectedDeadlockEventName),
                "Detected application deadlock on message {Message} and activation {Activation}");

        private static readonly Action<ILogger, string, Message.RejectionTypes, Message, Exception> LogDispatcherDiscardedRejection =
            LoggerMessage.Define<string, Message.RejectionTypes, Message>(
                LogLevel.Warning,
                new EventId((int)ErrorCode.Messaging_Dispatcher_DiscardRejection, DispatcherDiscardedRejectionEventName),
                "Discarding rejection {Reason} of type {RejectionType} for message {Message}");

        private static readonly Action<ILogger, string, Message.RejectionTypes, Message, Exception> LogDispatcherRejectedMessage =
            LoggerMessage.Define<string, Message.RejectionTypes, Message>(
                LogLevel.Debug,
                new EventId((int)ErrorCode.Messaging_Dispatcher_Rejected, DispatcherRejectedMessageEventName),
                "Rejected message {Message} with reason '{Reason}' ({RejectionType})");

        private static readonly Action<ILogger, Message, ActivationAddress, ActivationAddress, string, int, Exception> LogDispatcherForwarding =
            LoggerMessage.Define<Message, ActivationAddress, ActivationAddress, string, int>(
                LogLevel.Information,
                new EventId((int)ErrorCode.Messaging_Dispatcher_TryForward, DispatcherForwardingEventName),
                "Trying to forward {Message} from {OldAddress} to {ForwardingAddress} after {FailedOperation}. Attempt {ForwardCount}");

        private static readonly Action<ILogger, Message, ActivationAddress, ActivationAddress, string, int, Exception> LogDispatcherForwardingFailed =
            LoggerMessage.Define<Message, ActivationAddress, ActivationAddress, string, int>(
                LogLevel.Warning,
                new EventId((int)ErrorCode.Messaging_Dispatcher_TryForwardFailed, DispatcherForwardingFailedEventName),
                "Failed to forward message {Message} from {OldAddress} to {ForwardingAddress} after {FailedOperation}. Attempt {ForwardCount}");

        private static readonly Action<ILogger, int, ActivationAddress, ActivationAddress, string, Exception> LogDispatcherForwardingMultiple =
            LoggerMessage.Define<int, ActivationAddress, ActivationAddress, string>(
                LogLevel.Information,
                new EventId((int)ErrorCode.Messaging_Dispatcher_ForwardingRequests, DispatcherForwardingMultipleEventName),
                "Forwarding {MessageCount} requests destined for address {OldAddress} to address {ForwardingAddress} after {FailedOperation}");

        public RuntimeMessagingTrace(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }

        internal void OnDispatcherReceiveInvalidActivation(Message message, ActivationState activationState)
        {
            if (this.IsEnabled(DispatcherReceiveInvalidActivationEventName))
            {
                this.Write(DispatcherReceiveInvalidActivationEventName, new { Message = message, ActivationState = activationState });
            }

            MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(message);
            LogDispatcherReceiveInvalidActivation(this, activationState, message, null);
        }

        internal void OnDispatcherDetectedDeadlock(Message message, ActivationData targetActivation, DeadlockException exception)
        {
            if (this.IsEnabled(DispatcherDetectedDeadlockEventName))
            {
                this.Write(DispatcherDetectedDeadlockEventName, new { Message = message, Activation = targetActivation, Exception = exception});
            }

            MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(message);
            LogDispatcherDetectedDeadlock(this, message, targetActivation, exception);
        }

        internal void OnDispatcherDiscardedRejection(Message message, Message.RejectionTypes rejectionType, string reason, Exception exception)
        {
            if (this.IsEnabled(DispatcherDiscardedRejectionEventName))
            {
                this.Write(DispatcherDiscardedRejectionEventName, new { Message = message, RejectionType = rejectionType, Reason = reason, Exception = exception });
            }

            LogDispatcherDiscardedRejection(this, reason, rejectionType, message, exception);
        }

        internal void OnDispatcherRejectMessage(Message message, Message.RejectionTypes rejectionType, string reason, Exception exception)
        {
            if (this.IsEnabled(DispatcherRejectedMessageEventName))
            {
                this.Write(DispatcherRejectedMessageEventName, new { Message = message, RejectionType = rejectionType, Reason = reason, Exception = exception });
            }

            MessagingStatisticsGroup.OnRejectedMessage(message);

            if (this.IsEnabled(LogLevel.Debug))
            {
                LogDispatcherRejectedMessage(this, reason, rejectionType, message, exception);
            }
        }

        internal void OnDispatcherForwarding(Message message, ActivationAddress oldAddress, ActivationAddress forwardingAddress, string failedOperation, Exception exception)
        {
            if (this.IsEnabled(DispatcherForwardingEventName))
            {
                this.Write(DispatcherForwardingEventName, new { Message = message, OldAddress = oldAddress, ForwardingAddress = forwardingAddress, FailedOperation = failedOperation, Exception = exception });
            }

            if (this.IsEnabled(LogLevel.Information))
            {
                LogDispatcherForwarding(this, message, oldAddress, forwardingAddress, failedOperation, message.ForwardCount, exception);
            }
        }

        internal void OnDispatcherForwardingFailed(Message message, ActivationAddress oldAddress, ActivationAddress forwardingAddress, string failedOperation, Exception exception)
        {
            if (this.IsEnabled(DispatcherForwardingFailedEventName))
            {
                this.Write(DispatcherForwardingFailedEventName, new { Message = message, OldAddress = oldAddress, ForwardingAddress = forwardingAddress, FailedOperation = failedOperation, Exception = exception });
            }

            LogDispatcherForwardingFailed(this, message, oldAddress, forwardingAddress, failedOperation, message.ForwardCount, exception);
        }

        internal void OnDispatcherForwardingMultiple(int messageCount, ActivationAddress oldAddress, ActivationAddress forwardingAddress, string failedOperation, Exception exception)
        {
            if (this.IsEnabled(DispatcherForwardingMultipleEventName))
            {
                this.Write(DispatcherForwardingMultipleEventName, new { MessageCount = messageCount, OldAddress = oldAddress, ForwardingAddress = forwardingAddress, FailedOperation = failedOperation, Exception = exception });
            }

            if (this.IsEnabled(LogLevel.Information))
            {
                LogDispatcherForwardingMultiple(this, messageCount, oldAddress, forwardingAddress, failedOperation, exception);
            }
        }
    }
}
