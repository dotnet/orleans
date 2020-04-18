using Microsoft.Extensions.Logging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime.Messaging
{
    internal sealed class IncomingMessageHandler
    {
        private readonly MessageCenter messageCenter;
        private readonly ActivationDirectory directory;
        private readonly OrleansTaskScheduler scheduler;
        private readonly Dispatcher dispatcher;
        private readonly MessageFactory messageFactory;
        private readonly ILogger<IncomingMessageHandler> log;
        private readonly MessagingTrace messagingTrace;

        internal IncomingMessageHandler(
            MessageCenter mc,
            ActivationDirectory ad, 
            OrleansTaskScheduler sched, 
            Dispatcher dispatcher, 
            MessageFactory messageFactory,
            ILogger<IncomingMessageHandler> log,
            MessagingTrace messagingTrace)
        {
            this.messageCenter = mc;
            this.directory = ad;
            this.scheduler = sched;
            this.dispatcher = dispatcher;
            this.messageFactory = messageFactory;
            this.log = log;
            this.messagingTrace = messagingTrace;
        }

        public void ReceiveMessage(Message msg)
        {
            this.messagingTrace.OnIncomingMessageAgentReceiveMessage(msg);

            IGrainContext context;
            // Find the activation it targets; first check for a system activation, then an app activation
            if (msg.TargetGrain.IsSystemTarget())
            {
                SystemTarget target = this.directory.FindSystemTarget(msg.TargetActivation);
                if (target == null)
                {
                    MessagingStatisticsGroup.OnRejectedMessage(msg);
                    Message response = this.messageFactory.CreateRejectionResponse(msg, Message.RejectionTypes.Unrecoverable,
                        string.Format("SystemTarget {0} not active on this silo. Msg={1}", msg.TargetGrain, msg));
                    this.messageCenter.SendMessage(response);
                    this.log.Warn(ErrorCode.MessagingMessageFromUnknownActivation, "Received a message {0} for an unknown SystemTarget: {1}", msg, msg.TargetAddress);
                    return;
                }
                context = target;
                switch (msg.Direction)
                {
                    case Message.Directions.Request:
                        this.messagingTrace.OnEnqueueMessageOnActivation(msg, target);
                        this.scheduler.QueueWorkItem(new RequestWorkItem(target, msg));
                        break;

                    case Message.Directions.Response:
                        this.messagingTrace.OnEnqueueMessageOnActivation(msg, target);
                        this.scheduler.QueueWorkItem(new ResponseWorkItem(target, msg));
                        break;

                    default:
                        this.log.Error(ErrorCode.Runtime_Error_100097, "Invalid message: " + msg);
                        break;
                }
            }
            else
            {
                // Run this code on the target activation's context, if it already exists
                ActivationData targetActivation = this.directory.FindTarget(msg.TargetActivation);
                if (targetActivation != null)
                {
                    lock (targetActivation)
                    {
                        var target = targetActivation; // to avoid a warning about nulling targetActivation under a lock on it
                        if (target.State == ActivationState.Valid)
                        {
                            // Response messages are not subject to overload checks.
                            if (msg.Direction != Message.Directions.Response)
                            {
                                var overloadException = target.CheckOverloaded(this.log);
                                if (overloadException != null)
                                {
                                    // Send rejection as soon as we can, to avoid creating additional work for runtime
                                    this.dispatcher.RejectMessage(msg, Message.RejectionTypes.Overloaded, overloadException, "Target activation is overloaded " + target);
                                    return;
                                }
                            }

                            // Run ReceiveMessage in context of target activation
                            context = target;
                        }
                        else
                        {
                            // Can't use this activation - will queue for another activation
                            target = null;
                            context = null;
                        }

                        EnqueueReceiveMessage(msg, target, context);
                    }
                }
                else
                {
                    // No usable target activation currently, so run ReceiveMessage in system context
                    EnqueueReceiveMessage(msg, null, null);
                }
            }

            void EnqueueReceiveMessage(Message msg, ActivationData targetActivation, IGrainContext context)
            {
                this.messagingTrace.OnEnqueueMessageOnActivation(msg, context);
                targetActivation?.IncrementEnqueuedOnDispatcherCount();
                scheduler.QueueAction(() =>
                {
                    try
                    {
                        dispatcher.ReceiveMessage(msg);
                    }
                    finally
                    {
                        targetActivation?.DecrementEnqueuedOnDispatcherCount();
                    }
                },
                context);
            }
        }
    }
}
