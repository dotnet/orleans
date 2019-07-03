using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Text;

namespace Orleans.Runtime
{
    [EventSource(Name = "Microsoft-Orleans-Dispatcher")]
    internal sealed class OrleansDispatcherEvent : EventSource
    {
        public static readonly OrleansDispatcherEvent Log = new OrleansDispatcherEvent();
        public void ReceiveMessage() => WriteEvent(1);
    }

    [EventSource(Name = "Microsoft-Orleans-InsideRuntimeClient")]
    internal sealed class OrleansInsideRuntimeClientEvent : EventSource
    {
        public static readonly OrleansInsideRuntimeClientEvent Log = new OrleansInsideRuntimeClientEvent();
        public void SendRequest()
        {
            WriteEvent(1);
        }
        public void ReceiveResponse()
        {
            WriteEvent(2);
        }
        public void SendResponse()
        {
            WriteEvent(3);
        }
    }

    [EventSource(Name = "Microsoft-Orleans-GatewayAcceptor")]
    internal sealed class OrleansGatewayAcceptorEvent : EventSource
    {
        public static readonly OrleansGatewayAcceptorEvent Log = new OrleansGatewayAcceptorEvent();

        public void HandleMessage()
        {
            WriteEvent(1);
        }

    }

    [EventSource(Name = "Microsoft-Orleans-IncomingMessageAcceptor")]
    internal sealed class OrleansIncomingMessageAcceptorEvent : EventSource
    {
        public static readonly OrleansIncomingMessageAcceptorEvent Log = new OrleansIncomingMessageAcceptorEvent();

        public void HandleMessage()
        {
            WriteEvent(1);
        }
    }

    [EventSource(Name = "Microsoft-Orleans-IncomingMessageAgent")]
    internal sealed class OrleansIncomingMessageAgentEvent : EventSource
    {
        public static readonly OrleansIncomingMessageAgentEvent Log = new OrleansIncomingMessageAgentEvent();
        public void ReceiverMessage() => WriteEvent(1);
    }
}
