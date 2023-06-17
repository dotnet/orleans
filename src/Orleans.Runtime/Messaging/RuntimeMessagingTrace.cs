using System;
using System.Collections.Generic;
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
        public const string DispatcherSelectTargetFailedEventName = Category + ".Dispatcher.SelectTargetFailed";

        private static readonly Action<ILogger, ActivationState, Message, Exception> LogDispatcherReceiveInvalidActivation =
            LoggerMessage.Define<ActivationState, Message>(
                LogLevel.Warning,
                new EventId((int)ErrorCode.Dispatcher_Receive_InvalidActivation, DispatcherReceiveInvalidActivationEventName),
                "Invalid activation in state {State} for message {Message}");

        private static readonly Action<ILogger, Message, ActivationData, Exception> LogDispatcherDetectedDeadlock =
            LoggerMessage.Define<Message, ActivationData>(
                LogLevel.Warning,
                new EventId((int)ErrorCode.Dispatcher_DetectedDeadlock, DispatcherDetectedDeadlockEventName),
                "Detected application deadlock on message {Message} and activation {Activation}");

        private static readonly Action<ILogger, Message, string, Message.RejectionTypes, Exception> LogDispatcherDiscardedRejection =
            LoggerMessage.Define<Message, string, Message.RejectionTypes>(
                LogLevel.Warning,
                new EventId((int)ErrorCode.Messaging_Dispatcher_DiscardRejection, DispatcherDiscardedRejectionEventName),
                "Discarding rejection of message {Message} with reason '{Reason}' ({RejectionType})");

        private static readonly Action<ILogger, Message, string, Message.RejectionTypes, Exception> LogDispatcherRejectedMessage =
            LoggerMessage.Define<Message, string, Message.RejectionTypes>(
                LogLevel.Debug,
                new EventId((int)ErrorCode.Messaging_Dispatcher_Rejected, DispatcherRejectedMessageEventName),
                "Rejected message {Message} with reason '{Reason}' ({RejectionType})");

        private static readonly Action<ILogger, Message, GrainAddress, SiloAddress, string, int, Exception> LogDispatcherForwarding =
            LoggerMessage.Define<Message, GrainAddress, SiloAddress, string, int>(
                LogLevel.Debug,
                new EventId((int)ErrorCode.Messaging_Dispatcher_TryForward, DispatcherForwardingEventName),
                "Trying to forward {Message} from {OldAddress} to {ForwardingAddress} after {FailedOperation}. Attempt {ForwardCount}");

        private static readonly Action<ILogger, Message, GrainAddress, SiloAddress, string, int, Exception> LogDispatcherForwardingFailed =
            LoggerMessage.Define<Message, GrainAddress, SiloAddress, string, int>(
                LogLevel.Warning,
                new EventId((int)ErrorCode.Messaging_Dispatcher_TryForwardFailed, DispatcherForwardingFailedEventName),
                "Failed to forward message {Message} from {OldAddress} to {ForwardingAddress} after {FailedOperation}. Attempt {ForwardCount}");

        private static readonly Action<ILogger, int, GrainAddress, SiloAddress, string, Exception> LogDispatcherForwardingMultiple =
            LoggerMessage.Define<int, GrainAddress, SiloAddress, string>(
                LogLevel.Debug,
                new EventId((int)ErrorCode.Messaging_Dispatcher_ForwardingRequests, DispatcherForwardingMultipleEventName),
                "Forwarding {MessageCount} requests destined for address {OldAddress} to address {ForwardingAddress} after {FailedOperation}");

        private static readonly Action<ILogger, Message, Exception> LogDispatcherSelectTargetFailed =
            LoggerMessage.Define<Message>(
                LogLevel.Error,
                new EventId((int)ErrorCode.Dispatcher_SelectTarget_Failed, DispatcherSelectTargetFailedEventName),
                "Failed to address message {Message}");

        public RuntimeMessagingTrace(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }

        internal void OnDispatcherReceiveInvalidActivation(Message message, ActivationState activationState)
        {
            if (this.IsEnabled(DispatcherReceiveInvalidActivationEventName))
            {
                this.Write(DispatcherReceiveInvalidActivationEventName, new { Message = message, ActivationState = activationState });
            }

            MessagingProcessingInstruments.OnDispatcherMessageProcessedError(message);
            LogDispatcherReceiveInvalidActivation(this, activationState, message, null);
        }

        internal void OnDispatcherDiscardedRejection(Message message, Message.RejectionTypes rejectionType, string reason, Exception exception)
        {
            if (this.IsEnabled(DispatcherDiscardedRejectionEventName))
            {
                this.Write(DispatcherDiscardedRejectionEventName, new { Message = message, RejectionType = rejectionType, Reason = reason, Exception = exception });
            }

            LogDispatcherDiscardedRejection(this, message, reason, rejectionType, exception);
        }

        internal void OnDispatcherRejectMessage(Message message, Message.RejectionTypes rejectionType, string reason, Exception exception)
        {
            if (this.IsEnabled(DispatcherRejectedMessageEventName))
            {
                this.Write(DispatcherRejectedMessageEventName, new { Message = message, RejectionType = rejectionType, Reason = reason, Exception = exception });
            }

            MessagingInstruments.OnRejectedMessage(message);

            if (this.IsEnabled(LogLevel.Debug))
            {
                LogDispatcherRejectedMessage(this, message, reason, rejectionType, exception);
            }
        }

        internal void OnDispatcherForwarding(Message message, GrainAddress oldAddress, SiloAddress forwardingAddress, string failedOperation, Exception exception)
        {
            if (this.IsEnabled(DispatcherForwardingEventName))
            {
                this.Write(DispatcherForwardingEventName, new { Message = message, OldAddress = oldAddress, ForwardingAddress = forwardingAddress, FailedOperation = failedOperation, Exception = exception });
            }

            if (this.IsEnabled(LogLevel.Debug))
            {
                LogDispatcherForwarding(this, message, oldAddress, forwardingAddress, failedOperation, message.ForwardCount, exception);
            }

            MessagingProcessingInstruments.OnDispatcherMessageForwared(message);
        }

        internal void OnDispatcherForwardingFailed(Message message, GrainAddress oldAddress, SiloAddress forwardingAddress, string failedOperation, Exception exception)
        {
            if (this.IsEnabled(DispatcherForwardingFailedEventName))
            {
                this.Write(DispatcherForwardingFailedEventName, new { Message = message, OldAddress = oldAddress, ForwardingAddress = forwardingAddress, FailedOperation = failedOperation, Exception = exception });
            }

            LogDispatcherForwardingFailed(this, message, oldAddress, forwardingAddress, failedOperation, message.ForwardCount, exception);
        }

        internal void OnDispatcherForwardingMultiple(int messageCount, GrainAddress oldAddress, SiloAddress forwardingAddress, string failedOperation, Exception exception)
        {
            if (this.IsEnabled(DispatcherForwardingMultipleEventName))
            {
                this.Write(DispatcherForwardingMultipleEventName, new { MessageCount = messageCount, OldAddress = oldAddress, ForwardingAddress = forwardingAddress, FailedOperation = failedOperation, Exception = exception });
            }

            if (this.IsEnabled(LogLevel.Debug))
            {
                LogDispatcherForwardingMultiple(this, messageCount, oldAddress, forwardingAddress, failedOperation, exception);
            }
        }

        internal void OnDispatcherSelectTargetFailed(Message message, Exception exception)
        {
            if (this.IsEnabled(DispatcherSelectTargetFailedEventName))
            {
                this.Write(DispatcherSelectTargetFailedEventName, new { Message = message, Exception = exception });
            }

            if (ShouldLogError(exception))
            {
                LogDispatcherSelectTargetFailed(this, message, exception);
            }

            MessagingProcessingInstruments.OnDispatcherMessageProcessedError(message);

            static bool ShouldLogError(Exception ex)
            {
                return !(ex.GetBaseException() is KeyNotFoundException) &&
                       !(ex.GetBaseException() is ClientNotAvailableException);
            }
        }
    }
}
