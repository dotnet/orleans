using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime;

internal partial class MessagingTrace(ILoggerFactory loggerFactory, MessagingInstruments messagingInstruments, MessagingProcessingInstruments messagingProcessingInstruments)
{
    private const string LoggerCategoryName = "Orleans.Messaging";
    private const string ExpiredEventName = "Expired";
    private const string BlockedEventName = "Blocked";
    private const string EnqueuedInboundEventName = "EnqueuedInbound";
    private const string DequeuedInboundEventName = "DequeuedInbound";
    private const string SendingDroppedEventName = "SendingDropped";
    private const string RejectedDeadSiloEventName = "RejectedDeadSilo";

    protected ILogger Logger { get; } = loggerFactory.CreateLogger(LoggerCategoryName);
    protected MessagingInstruments MessagingInstrumentation { get; } = messagingInstruments;
    protected MessagingProcessingInstruments MessagingProcessingInstrumentation { get; } = messagingProcessingInstruments;

    public void OnIncomingMessageAgentReceiveMessage(Message message)
    {
        OrleansIncomingMessageAgentEvent.Log.ReceiveMessage(message);
        MessagingProcessingInstrumentation.OnImaMessageReceived(message);
    }

    public void OnDispatcherReceiveMessage(Message message)
    {
        OrleansDispatcherEvent.Instance.ReceiveMessage(message);
        MessagingProcessingInstrumentation.OnDispatcherMessageReceive(message);
    }

    internal void OnDropExpiredMessage(Message message, MessagingInstruments.Phase phase)
    {
        MessagingInstrumentation.OnMessageExpired(phase);
        LogDropExpiredMessage(Logger, message, phase);
    }

    internal void OnDropBlockedApplicationMessage(Message message)
    {
        LogDropBlockedApplicationMessage(Logger, message);
    }

    internal void OnSiloDropSendingMessage(SiloAddress localSiloAddress, Message message, string reason)
    {
        MessagingInstrumentation.OnDroppedSentMessage(message);
        LogSiloDropSendingMessage(Logger, localSiloAddress, message, reason);
    }

    public void OnEnqueueInboundMessage(Message message)
    {
        LogEnqueueInboundMessage(Logger, message);
    }

    public void OnDequeueInboundMessage(Message message)
    {
        LogDequeueInboundMessage(Logger, message);
    }

    public void OnEnqueueMessageOnActivation(Message message, IGrainContext context)
    {
        MessagingProcessingInstrumentation.OnImaMessageEnqueued(context);
    }

    public void OnRejectSendMessageToDeadSilo(SiloAddress localSilo, Message message)
    {
        MessagingInstrumentation.OnFailedSentMessage(message);
        LogRejectSendMessageToDeadSilo(
            Logger,
            localSilo,
            new DeadSiloLogRecord(message.TargetSilo),
            message);
    }

    internal void OnSendRequest(Message message)
    {
        OrleansInsideRuntimeClientEvent.Instance.SendRequest(message);
    }

    [LoggerMessage(
        EventId = (int)ErrorCode.Messaging_DroppingExpiredMessage,
        EventName = ExpiredEventName,
        Level = LogLevel.Warning,
        Message = "Dropping expired message {Message} at phase {Phase}"
    )]
    private static partial void LogDropExpiredMessage(ILogger logger, Message message, MessagingInstruments.Phase phase);

    [LoggerMessage(
        EventId = (int)ErrorCode.Messaging_DroppingBlockedMessage,
        EventName = BlockedEventName,
        Level = LogLevel.Warning,
        Message = "Dropping message {Message} since this silo is blocking application messages"
    )]
    private static partial void LogDropBlockedApplicationMessage(ILogger logger, Message message);

    [LoggerMessage(
        EventId = (int)ErrorCode.Messaging_Inbound_Enqueue,
        EventName = EnqueuedInboundEventName,
        Level = LogLevel.Trace,
        Message = "Enqueueing inbound message {Message}"
    )]
    private static partial void LogEnqueueInboundMessage(ILogger logger, Message message);

    [LoggerMessage(
        EventId = (int)ErrorCode.Messaging_Inbound_Dequeue,
        EventName = DequeuedInboundEventName,
        Level = LogLevel.Trace,
        Message = "Dequeueing inbound message {Message}"
    )]
    private static partial void LogDequeueInboundMessage(ILogger logger, Message message);

    [LoggerMessage(
        EventId = (int)ErrorCode.Messaging_OutgoingMS_DroppingMessage,
        EventName = SendingDroppedEventName,
        Level = LogLevel.Warning,
        Message = "Silo {SiloAddress} is dropping message {Message}. Reason: {Reason}"
    )]
    private static partial void LogSiloDropSendingMessage(ILogger logger, SiloAddress siloAddress, Message message, string reason);

    [LoggerMessage(
        EventId = (int)ErrorCode.MessagingSendingRejection,
        EventName = RejectedDeadSiloEventName,
        Level = LogLevel.Information,
        Message = "Silo {SiloAddress} is rejecting message to known-dead silo {DeadSilo}: {Message}"
    )]
    private static partial void LogRejectSendMessageToDeadSilo(ILogger logger, SiloAddress siloAddress, DeadSiloLogRecord deadSilo, Message message);

    private readonly struct DeadSiloLogRecord(SiloAddress? siloAddress)
    {
        public override string ToString() => siloAddress?.ToString() ?? "(unknown dead silo)";
    }
}
