using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Text;

namespace Orleans.Runtime
{
    [EventSource(Name = "Microsoft-Orleans-Dispatcher")]
    internal sealed class OrleansDispatcherEvent : EventSource
    {
        private static readonly OrleansDispatcherEvent Log = new OrleansDispatcherEvent();
        public static readonly Action ReceiveMessageAction = Log.ReceiveMessage;
        public void ReceiveMessage() => WriteEvent(1);
    }

    [EventSource(Name = "Microsoft-Orleans-InsideRuntimeClient")]
    internal sealed class OrleansInsideRuntimeClientEvent : EventSource
    {
        private static readonly OrleansInsideRuntimeClientEvent Log = new OrleansInsideRuntimeClientEvent();
        public static readonly Action SendRequestAction = Log.SendRequest;
        public static readonly Action ReceiveResponseAction = Log.ReceiveResponse;
        public static readonly Action SendResponseAction = Log.SendResponse;
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
        private static readonly OrleansGatewayAcceptorEvent Log = new OrleansGatewayAcceptorEvent();
        public static readonly Action HandleMessageAction = Log.HandleMessage;

        public void HandleMessage()
        {
            WriteEvent(1);
        }

    }

    [EventSource(Name = "Microsoft-Orleans-IncomingMessageAcceptor")]
    internal sealed class OrleansIncomingMessageAcceptorEvent : EventSource
    {
        private static readonly OrleansIncomingMessageAcceptorEvent Log = new OrleansIncomingMessageAcceptorEvent();
        public static readonly Action HandleMessageAction = Log.HandleMessage;
        public void HandleMessage()
        {
            WriteEvent(1);
        }
    }

    [EventSource(Name = "Microsoft-Orleans-IncomingMessageAgent")]
    internal sealed class OrleansIncomingMessageAgentEvent : EventSource
    {
        private static readonly OrleansIncomingMessageAgentEvent Log = new OrleansIncomingMessageAgentEvent();
        public static readonly Action ReceiverMessageAction = Log.ReceiverMessage;
        public void ReceiverMessage() => WriteEvent(1);
    }
}
