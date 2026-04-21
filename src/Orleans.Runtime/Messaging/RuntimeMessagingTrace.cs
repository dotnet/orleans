using Microsoft.Extensions.Logging;
using Orleans.Runtime.Diagnostics;

namespace Orleans.Runtime;

internal sealed partial class RuntimeMessagingTrace(ILoggerFactory loggerFactory) : MessagingTrace(loggerFactory)
{
    internal void OnDispatcherReceiveInvalidActivation(Message message, ActivationState activationState)
    {
        DispatcherEvents.EmitReceivedInvalidActivation(message, activationState);
        MessagingProcessingInstruments.OnDispatcherMessageProcessedError(message);
        LogDispatcherReceiveInvalidActivation(Logger, activationState, message);
    }

    internal void OnDispatcherDetectedDeadlock(Message message, ActivationData activation)
    {
        DispatcherEvents.EmitDetectedDeadlock(message, activation);
        LogDispatcherDetectedDeadlock(Logger, message, activation);
    }

    internal void OnDispatcherDiscardedRejection(Message message, Message.RejectionTypes rejectionType, string? reason, Exception? exception)
    {
        DispatcherEvents.EmitDiscardedRejection(message, rejectionType, reason, exception);
        LogDispatcherDiscardedRejection(Logger, message, new RejectionReasonLogRecord(reason), rejectionType, exception);
    }

    internal void OnDispatcherRejectMessage(Message message, Message.RejectionTypes rejectionType, string? reason, Exception? exception)
    {
        DispatcherEvents.EmitRejected(message, rejectionType, reason, exception);
        MessagingInstruments.OnRejectedMessage(message);
        LogDispatcherRejectedMessage(Logger, message, new RejectionReasonLogRecord(reason), rejectionType, exception);
    }

    internal void OnDispatcherForwarding(Message message, GrainAddress? oldAddress, SiloAddress? forwardingAddress, string failedOperation, Exception? exception)
    {
        DispatcherEvents.EmitForwarding(message, oldAddress, forwardingAddress, failedOperation, exception);
        LogDispatcherForwarding(
            Logger,
            message,
            new OldAddressLogRecord(oldAddress),
            new ForwardingAddressLogRecord(forwardingAddress),
            failedOperation,
            message.ForwardCount,
            exception);
        MessagingProcessingInstruments.OnDispatcherMessageForwared(message);
    }

    internal void OnDispatcherForwardingFailed(Message message, GrainAddress? oldAddress, SiloAddress? forwardingAddress, string failedOperation, Exception? exception)
    {
        DispatcherEvents.EmitForwardingFailed(message, oldAddress, forwardingAddress, failedOperation, exception);
        LogDispatcherForwardingFailed(
            Logger,
            message,
            new OldAddressLogRecord(oldAddress),
            new ForwardingAddressLogRecord(forwardingAddress),
            failedOperation,
            message.ForwardCount,
            exception);
    }

    internal void OnDispatcherForwardingMultiple(int messageCount, GrainAddress? oldAddress, SiloAddress? forwardingAddress, string failedOperation, Exception? exception)
    {
        DispatcherEvents.EmitForwardingMultiple(messageCount, oldAddress, forwardingAddress, failedOperation, exception);
        LogDispatcherForwardingMultiple(
            Logger,
            messageCount,
            new OldAddressLogRecord(oldAddress),
            new ForwardingAddressLogRecord(forwardingAddress),
            failedOperation,
            exception);
    }

    internal void OnDispatcherSelectTargetFailed(Message message, Exception exception)
    {
        DispatcherEvents.EmitSelectTargetFailed(message, exception);
        MessagingProcessingInstruments.OnDispatcherMessageProcessedError(message);
        if (ShouldLogError(exception))
        {
            LogDispatcherSelectTargetFailed(Logger, message, exception);
        }

        static bool ShouldLogError(Exception ex)
        {
            return ex.GetBaseException() is not KeyNotFoundException &&
                   ex.GetBaseException() is not ClientNotAvailableException;
        }
    }

    [LoggerMessage(
        EventId = (int)ErrorCode.Dispatcher_Receive_InvalidActivation,
        EventName = nameof(DispatcherEvents.ReceivedInvalidActivation),
        Level = LogLevel.Warning,
        Message = "Invalid activation in state {State} for message {Message}"
    )]
    private static partial void LogDispatcherReceiveInvalidActivation(ILogger logger, ActivationState state, Message message);

    [LoggerMessage(
        EventId = (int)ErrorCode.Dispatcher_DetectedDeadlock,
        EventName = nameof(DispatcherEvents.DetectedDeadlock),
        Level = LogLevel.Warning,
        Message = "Detected application deadlock on message {Message} and activation {Activation}"
    )]
    private static partial void LogDispatcherDetectedDeadlock(ILogger logger, Message message, ActivationData activation);

    [LoggerMessage(
        EventId = (int)ErrorCode.Messaging_Dispatcher_DiscardRejection,
        EventName = nameof(DispatcherEvents.DiscardedRejection),
        Level = LogLevel.Warning,
        Message = "Discarding rejection of message {Message} with reason '{Reason}' ({RejectionType})"
    )]
    private static partial void LogDispatcherDiscardedRejection(ILogger logger, Message message, RejectionReasonLogRecord reason, Message.RejectionTypes rejectionType, Exception? exception);

    [LoggerMessage(
        EventId = (int)ErrorCode.Messaging_Dispatcher_Rejected,
        EventName = nameof(DispatcherEvents.Rejected),
        Level = LogLevel.Debug,
        Message = "Rejected message {Message} with reason '{Reason}' ({RejectionType})"
    )]
    private static partial void LogDispatcherRejectedMessage(ILogger logger, Message message, RejectionReasonLogRecord reason, Message.RejectionTypes rejectionType, Exception? exception);

    [LoggerMessage(
        EventId = (int)ErrorCode.Messaging_Dispatcher_TryForward,
        EventName = nameof(DispatcherEvents.Forwarding),
        Level = LogLevel.Debug,
        Message = "Trying to forward {Message} from {OldAddress} to {ForwardingAddress} after {FailedOperation}. Attempt {ForwardCount}"
    )]
    private static partial void LogDispatcherForwarding(ILogger logger, Message message, OldAddressLogRecord oldAddress, ForwardingAddressLogRecord forwardingAddress, string failedOperation, int forwardCount, Exception? exception);

    [LoggerMessage(
        EventId = (int)ErrorCode.Messaging_Dispatcher_TryForwardFailed,
        EventName = nameof(DispatcherEvents.ForwardingFailed),
        Level = LogLevel.Warning,
        Message = "Failed to forward message {Message} from {OldAddress} to {ForwardingAddress} after {FailedOperation}. Attempt {ForwardCount}"
    )]
    private static partial void LogDispatcherForwardingFailed(ILogger logger, Message message, OldAddressLogRecord oldAddress, ForwardingAddressLogRecord forwardingAddress, string failedOperation, int forwardCount, Exception? exception);

    [LoggerMessage(
        EventId = (int)ErrorCode.Messaging_Dispatcher_ForwardingRequests,
        EventName = nameof(DispatcherEvents.ForwardingMultiple),
        Level = LogLevel.Debug,
        Message = "Forwarding {MessageCount} requests destined for address {OldAddress} to address {ForwardingAddress} after {FailedOperation}"
    )]
    private static partial void LogDispatcherForwardingMultiple(ILogger logger, int messageCount, OldAddressLogRecord oldAddress, ForwardingAddressLogRecord forwardingAddress, string failedOperation, Exception? exception);

    [LoggerMessage(
        EventId = (int)ErrorCode.Dispatcher_SelectTarget_Failed,
        EventName = nameof(DispatcherEvents.SelectTargetFailed),
        Level = LogLevel.Error,
        Message = "Failed to address message {Message}"
    )]
    private static partial void LogDispatcherSelectTargetFailed(ILogger logger, Message message, Exception exception);

    private readonly struct RejectionReasonLogRecord(string? reason)
    {
        public override string ToString() => string.IsNullOrWhiteSpace(reason) ? "(unspecified)" : $"\"{reason}\"";
    }

    private readonly struct OldAddressLogRecord(GrainAddress? oldAddress)
    {
        public override string ToString() => oldAddress?.ToString() ?? "(unknown)";
    }

    private readonly struct ForwardingAddressLogRecord(SiloAddress? forwardingAddress)
    {
        public override string ToString() => forwardingAddress?.ToString() ?? "(unknown)";
    }
}
