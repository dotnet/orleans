using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Text;

namespace Orleans.Runtime
{
    [EventSource(Name = "Microsoft-Orleans-CallBackData")]
    internal sealed class OrleansCallBackDataEvent : EventSource
    {
        private static readonly OrleansCallBackDataEvent Log = new OrleansCallBackDataEvent();
        public static readonly Action OnTimeoutAction = Log.OnTimeout;
        public static readonly Action OnTargetSiloFailAction = Log.OnTargetSiloFail;
        public static readonly Action DoCallbackAction = Log.DoCallback;
        public void OnTimeout()
        {
            WriteEvent(1);
        }

        public void OnTargetSiloFail()
        {
            WriteEvent(2);
        }

        public void DoCallback()
        {
            WriteEvent(3);
        }
    }

    [EventSource(Name = "Microsoft-Orleans-OutsideRuntimeClient")]
    internal sealed class OrleansOutsideRuntimeClientEvent : EventSource
    {
        private static readonly OrleansOutsideRuntimeClientEvent Log = new OrleansOutsideRuntimeClientEvent();
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

    internal static class EventSourceUtils
    {
        public static void EmitEvent(Message message, Action emitAction)
        {
            EventSource.SetCurrentThreadActivityId(message.TraceContext?.ActivityId??Guid.Empty, out var previousId);
            emitAction();
            EventSource.SetCurrentThreadActivityId(previousId);
        }
    }
}
