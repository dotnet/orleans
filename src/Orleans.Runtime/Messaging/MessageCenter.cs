using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    internal class MessageCenter : IMessageCenter, IDisposable
    {
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly MessageFactory messageFactory;
        private readonly ConnectionManager connectionManager;
        private readonly MessagingTrace messagingTrace;
        private readonly SiloAddress _siloAddress;
        private readonly ILogger log;
        private Dispatcher dispatcher;
        private bool stopped;
        private HostedClient hostedClient;
        private Action<Message> sniffIncomingMessageHandler;

        public MessageCenter(
            ILocalSiloDetails siloDetails,
            MessageFactory messageFactory,
            Factory<MessageCenter, Gateway> gatewayFactory,
            ILogger<MessageCenter> logger,
            ISiloStatusOracle siloStatusOracle,
            ConnectionManager senderManager,
            MessagingTrace messagingTrace)
        {
            this.siloStatusOracle = siloStatusOracle;
            this.connectionManager = senderManager;
            this.messagingTrace = messagingTrace;
            this.log = logger;
            this.messageFactory = messageFactory;
            this._siloAddress = siloDetails.SiloAddress;

            if (siloDetails.GatewayAddress != null)
            {
                Gateway = gatewayFactory(this);
                Gateway.Start();
            }
        }

        public Gateway Gateway { get; }

        internal Dispatcher Dispatcher
        {
            get
            {
                return this.dispatcher ?? ThrowNullReferenceException();

                [MethodImpl(MethodImplOptions.NoInlining)]
                static Dispatcher ThrowNullReferenceException() => throw new NullReferenceException("MessageCenter.Dispatcher is null");
            }

            set => dispatcher = value;
        }

        internal bool IsBlockingApplicationMessages { get; private set; }

        public void SetHostedClient(HostedClient client) => this.hostedClient = client;

        public bool TryDeliverToProxy(Message msg)
        {
            if (!msg.TargetGrain.IsClient()) return false;
            if (this.Gateway is Gateway gateway && gateway.TryDeliverToProxy(msg)) return true;
            return this.hostedClient is HostedClient client && client.TryDispatchToClient(msg);
        }


        public void Start()
        {
        }

        public void Stop()
        {
            BlockApplicationMessages();
            StopAcceptingClientMessages();
            stopped = true;
        }

        /// <summary>
        /// Indicates that application messages should be blocked from being sent or received.
        /// This method is used by the "fast stop" process.
        /// <para>
        /// Specifically, all outbound application messages are dropped, except for rejections and messages to the membership table grain.
        /// Inbound application requests are rejected, and other inbound application messages are dropped.
        /// </para>
        /// </summary>
        public void BlockApplicationMessages()
        {
            if (log.IsEnabled(LogLevel.Debug))
            {
                log.LogDebug("BlockApplicationMessages");
            }

            IsBlockingApplicationMessages = true;
        }

        public void StopAcceptingClientMessages()
        {
            if (log.IsEnabled(LogLevel.Debug))
            {
                log.LogDebug("StopClientMessages");
            }

            try
            {
                Gateway?.Stop();
            }
            catch (Exception exc)
            {
                log.LogError((int)ErrorCode.Runtime_Error_100109, exc, "Stop failed");
            }
        }

        public void DispatchLocalMessage(Message message) => this.Dispatcher.ReceiveMessage(message);

        public void RerouteMessage(Message message) => this.Dispatcher.RerouteMessage(message);

        public Action<Message> SniffIncomingMessage
        {
            set
            {
                if (this.sniffIncomingMessageHandler != null)
                {
                    throw new InvalidOperationException("MessageCenter.SniffIncomingMessage already set");
                }

                this.sniffIncomingMessageHandler = value;
            }

            get => this.sniffIncomingMessageHandler;
        }

        public void SendMessage(Message msg)
        {
            // Note that if we identify or add other grains that are required for proper stopping, we will need to treat them as we do the membership table grain here.
            if (IsBlockingApplicationMessages && (msg.Category == Message.Categories.Application) && (msg.Result != Message.ResponseTypes.Rejection)
                && !Constants.SystemMembershipTableType.Equals(msg.TargetGrain))
            {
                // Drop the message on the floor if it's an application message that isn't a rejection
                this.messagingTrace.OnDropBlockedApplicationMessage(msg);
            }
            else
            {
                msg.SendingSilo ??= _siloAddress;

                if (stopped)
                {
                    log.LogInformation((int)ErrorCode.Runtime_Error_100115, "Message was queued for sending after outbound queue was stopped: {Message}", msg);
                    SendRejection(msg, Message.RejectionTypes.Unrecoverable, "Message was queued for sending after outbound queue was stopped");
                    return;
                }

                // Don't process messages that have already timed out
                if (msg.IsExpired)
                {
                    this.messagingTrace.OnDropExpiredMessage(msg, MessagingStatisticsGroup.Phase.Send);
                    return;
                }

                // First check to see if it's really destined for a proxied client, instead of a local grain.
                if (TryDeliverToProxy(msg))
                {
                    // Message was successfully delivered to the proxy.
                    return;
                }

                if (msg.TargetSilo is not { } targetSilo)
                {
                    log.LogError((int)ErrorCode.Runtime_Error_100113, "Message does not have a target silo: " + msg + " -- Call stack is: " + Utils.GetStackTrace());
                    SendRejection(msg, Message.RejectionTypes.Unrecoverable, "Message to be sent does not have a target silo");
                    return;
                }

                messagingTrace.OnSendMessage(msg);
                if (targetSilo.Matches(_siloAddress))
                {
                    if (log.IsEnabled(LogLevel.Trace))
                    {
                        log.LogTrace("Message has been looped back to this silo: {Message}", msg);
                    }

                    MessagingStatisticsGroup.LocalMessagesSent.Increment();
                    this.DispatchLocalMessage(msg);
                }
                else
                {
                    if (stopped)
                    {
                        log.LogInformation((int)ErrorCode.Runtime_Error_100115, "Message was queued for sending after outbound queue was stopped: {Message}", msg);
                        SendRejection(msg, Message.RejectionTypes.Unrecoverable, "Message was queued for sending after outbound queue was stopped");
                        return;
                    }

                    if (this.connectionManager.TryGetConnection(targetSilo, out var existingConnection))
                    {
                        existingConnection.Send(msg);
                        return;
                    }
                    else if (this.siloStatusOracle.IsDeadSilo(targetSilo))
                    {
                        // Do not try to establish 
                        this.messagingTrace.OnRejectSendMessageToDeadSilo(_siloAddress, msg);
                        this.SendRejection(msg, Message.RejectionTypes.Transient, "Target silo is known to be dead");
                        return;
                    }
                    else
                    {
                        var connectionTask = this.connectionManager.GetConnection(targetSilo);
                        if (connectionTask.IsCompletedSuccessfully)
                        {
                            var sender = connectionTask.Result;
                            sender.Send(msg);
                        }
                        else
                        {
                            _ = SendAsync(this, connectionTask, msg);

                            static async Task SendAsync(MessageCenter messageCenter, ValueTask<Connection> connectionTask, Message msg)
                            {
                                try
                                {
                                    var sender = await connectionTask;
                                    sender.Send(msg);
                                }
                                catch (Exception exception)
                                {
                                    messageCenter.SendRejection(msg, Message.RejectionTypes.Transient, $"Exception while sending message: {exception}");
                                }
                            }
                        }
                    }
                }
            }
        }

        internal void SendRejection(Message msg, Message.RejectionTypes rejectionType, string reason)
        {
            MessagingStatisticsGroup.OnRejectedMessage(msg);

            if (msg.Direction == Message.Directions.Response && msg.Result == Message.ResponseTypes.Rejection)
            {
                // Do not send reject a rejection locally, it will create a stack overflow
                MessagingStatisticsGroup.OnDroppedSentMessage(msg);
                if (this.log.IsEnabled(LogLevel.Debug)) log.Debug("Dropping rejection {msg}", msg);
            }
            else
            {
                if (string.IsNullOrEmpty(reason)) reason = $"Rejection from silo {this._siloAddress} - Unknown reason.";
                var error = this.messageFactory.CreateRejectionResponse(msg, rejectionType, reason);
                // rejection msgs are always originated in the local silo, they are never remote.
                this.DispatchLocalMessage(error);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
