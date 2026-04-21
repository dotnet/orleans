using System;
using Microsoft.Extensions.Logging;
using Orleans.Core.Diagnostics;

namespace Orleans.Runtime;

internal partial class MessagingTrace(ILoggerFactory loggerFactory)
{
    protected ILogger Logger { get; } = loggerFactory.CreateLogger(MessagingEvents.ListenerName);

    public void OnSendMessage(Message message)
    {
        MessagingEvents.EmitSent(message);
    }

    public void OnIncomingMessageAgentReceiveMessage(Message message)
    {
        MessagingEvents.EmitReceivedByIncomingAgent(message);
        OrleansIncomingMessageAgentEvent.Log.ReceiveMessage(message);
        MessagingProcessingInstruments.OnImaMessageReceived(message);
    }

    public void OnDispatcherReceiveMessage(Message message)
    {
        MessagingEvents.EmitReceivedByDispatcher(message);
        OrleansDispatcherEvent.Instance.ReceiveMessage(message);
        MessagingProcessingInstruments.OnDispatcherMessageReceive(message);
    }

    internal void OnDropExpiredMessage(Message message, MessagingInstruments.Phase phase)
    {
        MessagingEvents.EmitExpired(message, phase);
        MessagingInstruments.OnMessageExpired(phase);
        LogDropExpiredMessage(Logger, message, phase);
    }

    internal void OnDropBlockedApplicationMessage(Message message)
    {
        MessagingEvents.EmitBlocked(message);
        LogDropBlockedApplicationMessage(Logger, message);
    }

    internal void OnSiloDropSendingMessage(SiloAddress localSiloAddress, Message message, string reason)
    {
        MessagingEvents.EmitSendingDropped(localSiloAddress, message, reason);
        MessagingInstruments.OnDroppedSentMessage(message);
        LogSiloDropSendingMessage(Logger, localSiloAddress, message, reason);
    }

    public void OnEnqueueInboundMessage(Message message)
    {
        MessagingEvents.EmitEnqueuedInbound(message);
        LogEnqueueInboundMessage(Logger, message);
    }

    public void OnDequeueInboundMessage(Message message)
    {
        MessagingEvents.EmitDequeuedInbound(message);
        LogDequeueInboundMessage(Logger, message);
    }

    internal void OnCreateMessage(Message message)
    {
        MessagingEvents.EmitCreated(message);
    }

    public void OnScheduleMessage(Message message)
    {
        MessagingEvents.EmitScheduled(message);
    }

    public void OnEnqueueMessageOnActivation(Message message, IGrainContext context)
    {
        MessagingEvents.EmitEnqueuedOnActivation(message, context);
        MessagingProcessingInstruments.OnImaMessageEnqueued(context);
    }

    public void OnInvokeMessage(Message message)
    {
        MessagingEvents.EmitInvoked(message);
    }

    public void OnRejectSendMessageToDeadSilo(SiloAddress localSilo, Message message)
    {
        MessagingInstruments.OnFailedSentMessage(message);
        MessagingEvents.EmitRejectedDeadSilo(localSilo, message);
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
        EventName = nameof(MessagingEvents.Expired),
        Level = LogLevel.Warning,
        Message = "Dropping expired message {Message} at phase {Phase}"
    )]
    private static partial void LogDropExpiredMessage(ILogger logger, Message message, MessagingInstruments.Phase phase);

    [LoggerMessage(
        EventId = (int)ErrorCode.Messaging_DroppingBlockedMessage,
        EventName = nameof(MessagingEvents.Blocked),
        Level = LogLevel.Warning,
        Message = "Dropping message {Message} since this silo is blocking application messages"
    )]
    private static partial void LogDropBlockedApplicationMessage(ILogger logger, Message message);

    [LoggerMessage(
        EventId = (int)ErrorCode.Messaging_Inbound_Enqueue,
        EventName = nameof(MessagingEvents.EnqueuedInbound),
        Level = LogLevel.Trace,
        Message = "Enqueueing inbound message {Message}"
    )]
    private static partial void LogEnqueueInboundMessage(ILogger logger, Message message);

    [LoggerMessage(
        EventId = (int)ErrorCode.Messaging_Inbound_Dequeue,
        EventName = nameof(MessagingEvents.DequeuedInbound),
        Level = LogLevel.Trace,
        Message = "Dequeueing inbound message {Message}"
    )]
    private static partial void LogDequeueInboundMessage(ILogger logger, Message message);

    [LoggerMessage(
        EventId = (int)ErrorCode.Messaging_OutgoingMS_DroppingMessage,
        EventName = nameof(MessagingEvents.SendingDropped),
        Level = LogLevel.Warning,
        Message = "Silo {SiloAddress} is dropping message {Message}. Reason: {Reason}"
    )]
    private static partial void LogSiloDropSendingMessage(ILogger logger, SiloAddress siloAddress, Message message, string reason);

    [LoggerMessage(
        EventId = (int)ErrorCode.MessagingSendingRejection,
        EventName = nameof(MessagingEvents.RejectedDeadSilo),
        Level = LogLevel.Information,
        Message = "Silo {SiloAddress} is rejecting message to known-dead silo {DeadSilo}: {Message}"
    )]
    private static partial void LogRejectSendMessageToDeadSilo(ILogger logger, SiloAddress siloAddress, DeadSiloLogRecord deadSilo, Message message);

    private readonly struct DeadSiloLogRecord(SiloAddress? siloAddress)
    {
        public override string ToString() => siloAddress?.ToString() ?? "(unknown dead silo)";
    }
}
