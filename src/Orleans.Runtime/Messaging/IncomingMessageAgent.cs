using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime.Messaging
{
    internal class IncomingMessageAgent : TaskSchedulerAgent
    {
        private readonly IMessageCenter messageCenter;
        private readonly ActivationDirectory directory;
        private readonly OrleansTaskScheduler scheduler;
        private readonly Dispatcher dispatcher;
        private readonly MessageFactory messageFactory;
        private readonly MessagingTrace messagingTrace;
        private readonly Message.Categories category;

        internal IncomingMessageAgent(
            Message.Categories cat, 
            IMessageCenter mc,
            ActivationDirectory ad, 
            OrleansTaskScheduler sched, 
            Dispatcher dispatcher, 
            MessageFactory messageFactory,
            ILoggerFactory loggerFactory,
            MessagingTrace messagingTrace) :
            base(cat.ToString(), loggerFactory)
        {
            category = cat;
            messageCenter = mc;
            directory = ad;
            scheduler = sched;
            this.dispatcher = dispatcher;
            this.messageFactory = messageFactory;
            this.messagingTrace = messagingTrace;
            OnFault = FaultBehavior.RestartOnFault;
            messageCenter.RegisterLocalMessageHandler(cat, ReceiveMessage);
        }

        public override void Start()
        {
            base.Start();
            if (Log.IsEnabled(LogLevel.Trace)) Log.Trace("Started incoming message agent for silo at {0} for {1} messages", messageCenter.MyAddress, category);
        }

        protected override async Task Run()
        {
            var reader = messageCenter.GetReader(category);
            while (true)
            {
                var moreTask = reader.WaitToReadAsync();
                var more = moreTask.IsCompletedSuccessfully ? moreTask.GetAwaiter().GetResult() : await moreTask;
                if (!more) return;

                // Get an application message
                while (reader.TryRead(out var msg))
                {
                    this.messagingTrace.OnDequeueInboundMessage(msg);
                    if (msg == null)
                    {
                        if (Log.IsEnabled(LogLevel.Debug)) Log.Debug("Dequeued a null message, exiting");
                        // Null return means cancelled
                        continue;
                    }
                    else
                    {
                        ReceiveMessage(msg);
                    }
                }
            }
        }

        private void ReceiveMessage(Message msg)
        {
            this.messagingTrace.OnIncomingMessageAgentReceiveMessage(msg);

            ISchedulingContext context;
            // Find the activation it targets; first check for a system activation, then an app activation
            if (msg.TargetGrain.IsSystemTarget)
            {
                SystemTarget target = directory.FindSystemTarget(msg.TargetActivation);
                if (target == null)
                {
                    MessagingStatisticsGroup.OnRejectedMessage(msg);
                    Message response = this.messageFactory.CreateRejectionResponse(msg, Message.RejectionTypes.Unrecoverable,
                        String.Format("SystemTarget {0} not active on this silo. Msg={1}", msg.TargetGrain, msg));
                    messageCenter.SendMessage(response);
                    Log.Warn(ErrorCode.MessagingMessageFromUnknownActivation, "Received a message {0} for an unknown SystemTarget: {1}", msg, msg.TargetAddress);
                    return;
                }
                context = target.SchedulingContext;
                switch (msg.Direction)
                {
                    case Message.Directions.Request:
                        this.messagingTrace.OnEnqueueMessageOnActivation(msg, context);
                        scheduler.QueueWorkItem(new RequestWorkItem(target, msg), context);
                        break;

                    case Message.Directions.Response:
                        this.messagingTrace.OnEnqueueMessageOnActivation(msg, context);
                        scheduler.QueueWorkItem(new ResponseWorkItem(target, msg), context);
                        break;

                    default:
                        Log.Error(ErrorCode.Runtime_Error_100097, "Invalid message: " + msg);
                        break;
                }
            }
            else
            {
                // Run this code on the target activation's context, if it already exists
                ActivationData targetActivation = directory.FindTarget(msg.TargetActivation);
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
                                var overloadException = target.CheckOverloaded(Log);
                                if (overloadException != null)
                                {
                                    // Send rejection as soon as we can, to avoid creating additional work for runtime
                                    dispatcher.RejectMessage(msg, Message.RejectionTypes.Overloaded, overloadException, "Target activation is overloaded " + target);
                                    return;
                                }
                            }

                            // Run ReceiveMessage in context of target activation
                            context = target.SchedulingContext;
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
        }

        private void EnqueueReceiveMessage(Message msg, ActivationData targetActivation, ISchedulingContext context)
        {
            this.messagingTrace.OnEnqueueMessageOnActivation(msg, context);
            targetActivation?.IncrementEnqueuedOnDispatcherCount();
            scheduler.QueueWorkItem(new ClosureWorkItem(() =>
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
            "Dispatcher.ReceiveMessage"), context);
        }
    }
}
