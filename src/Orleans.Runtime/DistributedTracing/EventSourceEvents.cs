using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Text;

namespace Orleans.DistributedTracing.EventSourceEvents
{
    [EventSource(Name = "Microsoft-Orleans-DispatcherEvent")]
    public class OrleansDispatcherEvent : EventSource
    {
        public static readonly OrleansDispatcherEvent Log = new OrleansDispatcherEvent();
        public void ReceiveMessageStart() => WriteEvent(1);
        public void ReceiveMessageStop() => WriteEvent(2);
    }

    [EventSource(Name = "Microsoft-Orleans-InsideRuntimeClientEvent")]
    public class OrleansInsideRuntimeClientEvent : EventSource
    {
        public static readonly OrleansInsideRuntimeClientEvent Log = new OrleansInsideRuntimeClientEvent();
        public void SendRequestStart()
        {
            WriteEvent(1);
        }

        public void SendRequestStop()
        {
            WriteEvent(2);
        }
        public void ReceiveResponseStart()
        {
            WriteEvent(3);
        }
        public void ReceiveResponseStop()
        {
            WriteEvent(4);
        }

        public void SendResponseStart()
        {
            WriteEvent(5);
        }
        public void SendResponseStop()
        {
            WriteEvent(6);
        }
    }

    [EventSource(Name = "Microsoft-Orleans-GatewayAcceptorEvent")]
    public class OrleansGatewayAcceptorEvent : EventSource
    {
        public static readonly OrleansGatewayAcceptorEvent Log = new OrleansGatewayAcceptorEvent();

        public void HandleMessageStart()
        {
            WriteEvent(1);
        }
        public void HandleMessageStop()
        {
            WriteEvent(2);
        }

    }

    [EventSource(Name = "Microsoft-Orleans-IncomingMessageAcceptorEvent")]
    public class OrleansIncomingMessageAcceptorEvent : EventSource
    {
        public static readonly OrleansIncomingMessageAcceptorEvent Log = new OrleansIncomingMessageAcceptorEvent();

        public void HandleMessageStart()
        {
            WriteEvent(1);
        }

        public void HandleMessageStop()
        {
            WriteEvent(2);
        }
    }

    [EventSource(Name = "Microsoft-Orleans-IncomingMessageAgentEvent")]
    public class OrleansIncomingMessageAgentEvent : EventSource
    {
        public static readonly OrleansIncomingMessageAgentEvent Log = new OrleansIncomingMessageAgentEvent();
        public void ReceiverMessageStart() => WriteEvent(1);
        public void ReceiverMessageStop() => WriteEvent(2);
    }

    [EventSource(Name = "Microsoft-Orleans-ActivationTaskSchedulerEvent")]
    public class OrleansActivationTaskSchedulerEvent : EventSource
    {
        public static readonly OrleansActivationTaskSchedulerEvent Log = new OrleansActivationTaskSchedulerEvent();
        public void QueueTaskStart(int taskId) => WriteEvent(1, taskId);
        public void QueueTaskStop(int taskId) => WriteEvent(2, taskId);
        public void RunTaskStart(int taskId) => WriteEvent(3, taskId);
        public void RunTaskStop(int taskId) => WriteEvent(4, taskId);

    }
}
