using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class MessagingTrace : DiagnosticListener, ILogger
    {
        public const string Category = "Orleans.Messaging";

        public const string CreateMessageEventName = Category + ".CreateMessage";
        public const string SendMessageEventName = Category + ".Outbound.Send";
        public const string IncomingMessageAgentReceiveMessageEventName = Category + ".IncomingMessageAgent.Receive";
        public const string DispatcherReceiveMessageEventName = Category + ".Dispatcher.Receive";
        public const string DropExpiredMessageEventName = Category + ".Drop.Expired";
        public const string DropSendingMessageEventName = Category + ".Drop.Sending";
        public const string DropBlockedApplicationMessageEventName = Category + ".Drop.Blocked";
        public const string EnqueueInboundMessageEventName = Category + ".Inbound.Enqueue";
        public const string DequeueInboundMessageEventName = Category + ".Inbound.Dequeue";
        public const string ScheduleMessageEventName = Category + ".Schedule";
        public const string EnqueueMessageOnActivationEventName = Category + ".Activation.Enqueue";
        public const string InvokeMessageEventName = Category + ".Invoke";
        public const string RejectSendMessageToDeadSiloEventName = Category + ".Reject.TargetDead";

        private static readonly Action<ILogger, Message, MessagingInstruments.Phase, Exception> LogDropExpiredMessage
            = LoggerMessage.Define<Message, MessagingInstruments.Phase>(
                LogLevel.Warning,
                new EventId((int)ErrorCode.Messaging_DroppingExpiredMessage, DropExpiredMessageEventName),
                "Dropping expired message {Message} at phase {Phase}");

        private static readonly Action<ILogger, Message, Exception> LogDropBlockedApplicationMessage
            = LoggerMessage.Define<Message>(
                LogLevel.Warning,
                new EventId((int)ErrorCode.Messaging_DroppingBlockedMessage, DropBlockedApplicationMessageEventName),
                "Dropping message {Message} since this silo is blocking application messages");

        private static readonly Action<ILogger, Message, Exception> LogEnqueueInboundMessage
            = LoggerMessage.Define<Message>(
                LogLevel.Trace,
                new EventId((int)ErrorCode.Messaging_Inbound_Enqueue, EnqueueInboundMessageEventName),
                "Enqueueing inbound message {Message}");

        private static readonly Action<ILogger, Message, Exception> LogDequeueInboundMessage
            = LoggerMessage.Define<Message>(
                LogLevel.Trace,
                new EventId((int)ErrorCode.Messaging_Inbound_Dequeue, DequeueInboundMessageEventName),
                "Dequeueing inbound message {Message}");

        private static readonly Action<ILogger, SiloAddress, Message, string, Exception> LogSiloDropSendingMessage
            = LoggerMessage.Define<SiloAddress, Message, string>(
                LogLevel.Warning,
                new EventId((int)ErrorCode.Messaging_OutgoingMS_DroppingMessage, DropSendingMessageEventName),
                "Silo {SiloAddress} is dropping message {Message}. Reason: {Reason}");

        private static readonly Action<ILogger, SiloAddress, SiloAddress, Message, Exception> LogRejectSendMessageToDeadSilo
            = LoggerMessage.Define<SiloAddress, SiloAddress, Message>(
                LogLevel.Information,
                new EventId((int)ErrorCode.MessagingSendingRejection, RejectSendMessageToDeadSiloEventName),
                  "Silo {SiloAddress} is rejecting message to known-dead silo {DeadSilo}: {Message}");


        private readonly ILogger log;

        public MessagingTrace(ILoggerFactory loggerFactory) : base(Category)
        {
            this.log = loggerFactory.CreateLogger(Category);
        }

        public void OnSendMessage(Message message)
        {
            if (this.IsEnabled(SendMessageEventName))
            {
                this.Write(SendMessageEventName, message);
            }
        }

        public void OnIncomingMessageAgentReceiveMessage(Message message)
        {
            if (this.IsEnabled(IncomingMessageAgentReceiveMessageEventName))
            {
                this.Write(IncomingMessageAgentReceiveMessageEventName, message);
            }

            OrleansIncomingMessageAgentEvent.Log.ReceiveMessage(message);
            MessagingProcessingInstruments.OnImaMessageReceived(message);
        }

        public void OnDispatcherReceiveMessage(Message message)
        {
            if (this.IsEnabled(DispatcherReceiveMessageEventName))
            {
                this.Write(DispatcherReceiveMessageEventName, message);
            }

            OrleansDispatcherEvent.Log.ReceiveMessage(message);
            MessagingProcessingInstruments.OnDispatcherMessageReceive(message);
        }

        internal void OnDropExpiredMessage(Message message, MessagingInstruments.Phase phase)
        {
            if (this.IsEnabled(DropExpiredMessageEventName))
            {
                this.Write(DropExpiredMessageEventName, new { Message = message, Phase = phase });
            }

            MessagingInstruments.OnMessageExpired(phase);
            LogDropExpiredMessage(this, message, phase, null);
        }

        internal void OnDropBlockedApplicationMessage(Message message)
        {
            if (this.IsEnabled(DropBlockedApplicationMessageEventName))
            {
                this.Write(DropBlockedApplicationMessageEventName, message);
            }

            LogDropBlockedApplicationMessage(this, message, null);
        }

        internal void OnSiloDropSendingMessage(SiloAddress localSiloAddress, Message message, string reason)
        {
            MessagingInstruments.OnDroppedSentMessage(message);
            LogSiloDropSendingMessage(this, localSiloAddress, message, reason, null);
        }

        public void OnEnqueueInboundMessage(Message message)
        {
            if (this.IsEnabled(EnqueueInboundMessageEventName))
            {
                this.Write(EnqueueInboundMessageEventName, message);
            }

            LogEnqueueInboundMessage(this, message, null);
        }

        public void OnDequeueInboundMessage(Message message)
        {
            if (this.IsEnabled(DequeueInboundMessageEventName))
            {
                this.Write(DequeueInboundMessageEventName, message);
            }

            LogDequeueInboundMessage(this, message, null);
        }

        internal void OnCreateMessage(Message message)
        {
            if (this.IsEnabled(CreateMessageEventName))
            {
                this.Write(CreateMessageEventName, message);
            }
        }

        public void OnScheduleMessage(Message message)
        {
            if (this.IsEnabled(ScheduleMessageEventName))
            {
                this.Write(ScheduleMessageEventName, message);
            }
        }

        public void OnEnqueueMessageOnActivation(Message message, IGrainContext context)
        {
            if (this.IsEnabled(EnqueueMessageOnActivationEventName))
            {
                this.Write(EnqueueMessageOnActivationEventName, message);
            }

            MessagingProcessingInstruments.OnImaMessageEnqueued(context);
        }

        public void OnInvokeMessage(Message message)
        {
            if (this.IsEnabled(InvokeMessageEventName))
            {
                this.Write(InvokeMessageEventName, message);
            }
        }

        public void OnRejectSendMessageToDeadSilo(SiloAddress localSilo, Message message)
        {
            MessagingInstruments.OnFailedSentMessage(message);

            if (this.IsEnabled(RejectSendMessageToDeadSiloEventName))
            {
                this.Write(RejectSendMessageToDeadSiloEventName, message);
            }

            LogRejectSendMessageToDeadSilo(
                this,
                localSilo,
                message.TargetSilo,
                message,
                null);
        }

        internal void OnSendRequest(Message message)
        {
            OrleansInsideRuntimeClientEvent.Log.SendRequest(message);
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return this.log.BeginScope(state);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsEnabled(LogLevel logLevel)
        {
            return this.log.IsEnabled(logLevel);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            this.log.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
