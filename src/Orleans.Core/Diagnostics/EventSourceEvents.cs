using System;
using System.Diagnostics.Tracing;

namespace Orleans.Runtime
{
    [EventSource(Name = "Microsoft-Orleans-CallBackData")]
    internal sealed class OrleansCallBackDataEvent : EventSource
    {
        public static readonly OrleansCallBackDataEvent Log = new OrleansCallBackDataEvent();

        [NonEvent]
        public void OnTimeout(Message message)
        {
            if (this.IsEnabled())
            {
                OnTimeout(message.TraceContext?.ActivityId ?? Guid.Empty);
            }
        }

        [Event(1)]
        public void OnTimeout(Guid activityId)
        {
            this.WriteEventWithRelatedActivityId(1, activityId);
        }

        [NonEvent]
        public void OnTargetSiloFail(Message message)
        {
            if (this.IsEnabled())
            {
                OnTargetSiloFail(message.TraceContext?.ActivityId ?? Guid.Empty);
            }
        }

        [Event(2)]
        public void OnTargetSiloFail(Guid activityId)
        {
            this.WriteEventWithRelatedActivityId(2, activityId);
        }

        [NonEvent]
        public void DoCallback(Message message)
        {
            if (this.IsEnabled())
            {
                DoCallback(message.TraceContext?.ActivityId ?? Guid.Empty);
            }
        }

        [Event(3)]
        public void DoCallback(Guid activityId)
        {
            WriteEventWithRelatedActivityId(3, activityId);
        }
    }

    [EventSource(Name = "Microsoft-Orleans-OutsideRuntimeClient")]
    internal sealed class OrleansOutsideRuntimeClientEvent : EventSource
    {
        public static readonly OrleansOutsideRuntimeClientEvent Log = new OrleansOutsideRuntimeClientEvent();

        [NonEvent]
        public void SendRequest(Message message)
        {
            if (this.IsEnabled())
            {
                SendRequest(message.TraceContext?.ActivityId ?? Guid.Empty);
            }
        }

        [Event(1)]
        public void SendRequest(Guid activityId)
        {
            WriteEventWithRelatedActivityId(1, activityId);
        }

        [NonEvent]
        public void ReceiveResponse(Message message)
        {
            if (this.IsEnabled())
            {
                ReceiveResponse(message.TraceContext?.ActivityId ?? Guid.Empty);
            }
        }

        [Event(2)]
        public void ReceiveResponse(Guid activityId)
        {
            WriteEventWithRelatedActivityId(2, activityId);
        }

        [NonEvent]
        public void SendResponse(Message message)
        {
            if (this.IsEnabled())
            {
                SendResponse(message.TraceContext?.ActivityId ?? Guid.Empty);
            }
        }

        [Event(3)]
        public void SendResponse(Guid activityId)
        {
            WriteEventWithRelatedActivityId(3, activityId);
        }
    }

    [EventSource(Name = "Microsoft-Orleans-Dispatcher")]
    internal sealed class OrleansDispatcherEvent : EventSource
    {
        public static readonly OrleansDispatcherEvent Log = new OrleansDispatcherEvent();

        [NonEvent]
        public void ReceiveMessage(Message message)
        {
            if (this.IsEnabled())
            {
                ReceiveMessage(message.TraceContext?.ActivityId ?? Guid.Empty);
            }
        }

        [Event(1)]
        public void ReceiveMessage(Guid activityId) => WriteEventWithRelatedActivityId(1, activityId);
    }

    [EventSource(Name = "Microsoft-Orleans-InsideRuntimeClient")]
    internal sealed class OrleansInsideRuntimeClientEvent : EventSource
    {
        public static readonly OrleansInsideRuntimeClientEvent Log = new OrleansInsideRuntimeClientEvent();

        [NonEvent]
        public void SendRequest(Message message)
        {
            if (this.IsEnabled())
            {
                SendRequest(message.TraceContext?.ActivityId ?? Guid.Empty);
            }
        }

        [Event(1)]
        public void SendRequest(Guid activityId) => WriteEventWithRelatedActivityId(1, activityId);

        [NonEvent]
        public void ReceiveResponse(Message message)
        {
            if (this.IsEnabled())
            {
                ReceiveResponse(message.TraceContext?.ActivityId ?? Guid.Empty);
            }
        }

        [Event(2)]
        public void ReceiveResponse(Guid activityId) => WriteEventWithRelatedActivityId(2, activityId);

        [NonEvent]
        public void SendResponse(Message message)
        {
            if (this.IsEnabled())
            {
                SendResponse(message.TraceContext?.ActivityId ?? Guid.Empty);
            }
        }

        [Event(3)]
        public void SendResponse(Guid activityId) => WriteEventWithRelatedActivityId(3, activityId);
    }

    [EventSource(Name = "Microsoft-Orleans-GatewayAcceptor")]
    internal sealed class OrleansGatewayAcceptorEvent : EventSource
    {
        public static readonly OrleansGatewayAcceptorEvent Log = new OrleansGatewayAcceptorEvent();

        [NonEvent]
        public void HandleMessage(Message message)
        {
            if (this.IsEnabled())
            {
                HandleMessage(message.TraceContext?.ActivityId ?? Guid.Empty);
            }
        }

        [Event(1)]
        public void HandleMessage(Guid activityId) => WriteEventWithRelatedActivityId(1, activityId);

    }

    [EventSource(Name = "Microsoft-Orleans-IncomingMessageAcceptor")]
    internal sealed class OrleansIncomingMessageAcceptorEvent : EventSource
    {
        public static readonly OrleansIncomingMessageAcceptorEvent Log = new OrleansIncomingMessageAcceptorEvent();

        [NonEvent]
        public void HandleMessage(Message message)
        {
            if (this.IsEnabled())
            {
                HandleMessage(message.TraceContext?.ActivityId ?? Guid.Empty);
            }
        }

        [Event(1)]
        public void HandleMessage(Guid activityId) => WriteEventWithRelatedActivityId(1, activityId);
    }

    [EventSource(Name = "Microsoft-Orleans-IncomingMessageAgent")]
    internal sealed class OrleansIncomingMessageAgentEvent : EventSource
    {
        public static readonly OrleansIncomingMessageAgentEvent Log = new OrleansIncomingMessageAgentEvent();

        [NonEvent]
        public void ReceiveMessage(Message message)
        {
            if (this.IsEnabled())
            {
                ReceiveMessage(message.TraceContext?.ActivityId ?? Guid.Empty);
            }
        }

        [Event(1)]
        public void ReceiveMessage(Guid activityId) => WriteEventWithRelatedActivityId(1, activityId);
    }
}
