using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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

        private static partial class Log
        {
            [LoggerMessage(1, LogLevel.Warning, "Dropping expired message {Message} at phase {Phase}")]
            public static partial void DropExpiredMessage(ILogger logger, Message message, MessagingInstruments.Phase phase);

            [LoggerMessage(2, LogLevel.Warning, "Dropping message {Message} since this silo is blocking application messages")]
            public static partial void DropBlockedApplicationMessage(ILogger logger, Message message);

            [LoggerMessage(3, LogLevel.Trace, "Enqueueing inbound message {Message}")]
            public static partial void EnqueueInboundMessage(ILogger logger, Message message);

            [LoggerMessage(4, LogLevel.Trace, "Dequeueing inbound message {Message}")]
            public static partial void DequeueInboundMessage(ILogger logger, Message message);

            [LoggerMessage(5, LogLevel.Warning, "Silo {SiloAddress} is dropping message {Message}. Reason: {Reason}")]
            public static partial void SiloDropSendingMessage(ILogger logger, SiloAddress localSiloAddress, Message message, string reason);

            [LoggerMessage(6, LogLevel.Information, "Silo {SiloAddress} is rejecting message to known-dead silo {DeadSilo}: {Message}")]
            public static partial void RejectSendMessageToDeadSilo(ILogger logger, SiloAddress localSilo, SiloAddress deadSilo, Message message);
        }

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
            Log.DropExpiredMessage(this.log, message, phase);
        }

        internal void OnDropBlockedApplicationMessage(Message message)
        {
            if (this.IsEnabled(DropBlockedApplicationMessageEventName))
            {
                this.Write(DropBlockedApplicationMessageEventName, message);
            }

            Log.DropBlockedApplicationMessage(this.log, message);
        }

        internal void OnSiloDropSendingMessage(SiloAddress localSiloAddress, Message message, string reason)
        {
            MessagingInstruments.OnDroppedSentMessage(message);
            Log.SiloDropSendingMessage(this.log, localSiloAddress, message, reason);
        }

        public void OnEnqueueInboundMessage(Message message)
        {
            if (this.IsEnabled(EnqueueInboundMessageEventName))
            {
                this.Write(EnqueueInboundMessageEventName, message);
            }

            Log.EnqueueInboundMessage(this.log, message);
        }

        public void OnDequeueInboundMessage(Message message)
        {
            if (this.IsEnabled(DequeueInboundMessageEventName))
            {
                this.Write(DequeueInboundMessageEventName, message);
            }

            Log.DequeueInboundMessage(this.log, message);
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

            Log.RejectSendMessageToDeadSilo(
                this.log,
                localSilo,
                message.TargetSilo,
                message);
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
