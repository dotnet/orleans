using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Text;

namespace Orleans.DistributedTracing.EventSourceEvents
{
    [EventSource(Name = "Microsoft-Orleans-CallBackDataEvent")]
    public class OrleansCallBackDataEvent : EventSource
    {
        public static readonly OrleansCallBackDataEvent Log = new OrleansCallBackDataEvent();
        public void OnTimeoutStart()
        {
            WriteEvent(1);
        }
        public void OnTimeoutStop()
        {
            WriteEvent(2);
        }

        public void OnTargetSiloFailStart()
        {
            WriteEvent(3);
        }
        public void OnTargetSiloFailStop()
        {
            WriteEvent(4);
        }

        public void DoCallbackStart()
        {
            WriteEvent(5);
        }
        public void DoCallbackStop()
        {
            WriteEvent(6);
        }
    }

    [EventSource(Name = "Microsoft-Orleans-OutsideRuntimeClientEvent")]
    public class OrleansOutsideRuntimeClientEvent : EventSource
    {
        public static readonly OrleansOutsideRuntimeClientEvent Log = new OrleansOutsideRuntimeClientEvent();
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
}
