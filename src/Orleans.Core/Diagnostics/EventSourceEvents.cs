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
                using (message.SetThreadActivityId())
                {
                    this.OnTimeout();
                }
            }
        }

        [Event(1, Level = EventLevel.Warning)]
        private void OnTimeout() => this.WriteEvent(1);

        [NonEvent]
        public void OnTargetSiloFail(Message message)
        {
            if (this.IsEnabled())
            {
                using (message.SetThreadActivityId())
                {
                    this.OnTargetSiloFail();
                }
            }
        }

        [Event(2, Level = EventLevel.Warning)]
        private void OnTargetSiloFail() => this.WriteEvent(2);

        [NonEvent]
        public void DoCallback(Message message)
        {
            if (this.IsEnabled())
            {
                using (message.SetThreadActivityId())
                {
                    this.DoCallback();
                }
            }
        }

        [Event(3, Level = EventLevel.Verbose)]
        private void DoCallback() => this.WriteEvent(3);
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
                using (message.SetThreadActivityId())
                {
                    this.SendRequest();
                }
            }
        }

        [Event(1, Level = EventLevel.Verbose)]
        private void SendRequest() => this.WriteEvent(1);

        [NonEvent]
        public void ReceiveResponse(Message message)
        {
            if (this.IsEnabled())
            {
                using (message.SetThreadActivityId())
                {
                    this.ReceiveResponse();
                }
            }
        }

        [Event(2, Level = EventLevel.Verbose)]
        private void ReceiveResponse() => this.WriteEvent(2);

        [NonEvent]
        public void SendResponse(Message message)
        {
            if (this.IsEnabled())
            {
                using (message.SetThreadActivityId())
                {
                    this.SendResponse();
                }
            }
        }

        [Event(3, Level = EventLevel.Verbose)]
        private void SendResponse() => this.WriteEvent(3);
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
                using (message.SetThreadActivityId())
                {
                    this.ReceiveMessage();
                }
            }
        }

        [Event(1, Level = EventLevel.Verbose)]
        private void ReceiveMessage() => WriteEvent(1);
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
                using (message.SetThreadActivityId())
                {
                    this.SendRequest();
                }
            }
        }

        [Event(1, Level = EventLevel.Verbose)]
        private void SendRequest() => WriteEvent(1);

        [NonEvent]
        public void ReceiveResponse(Message message)
        {
            if (this.IsEnabled())
            {
                using (message.SetThreadActivityId())
                {
                    this.ReceiveResponse();
                }
            }
        }

        [Event(2, Level = EventLevel.Verbose)]
        private void ReceiveResponse() => WriteEvent(2);

        [NonEvent]
        public void SendResponse(Message message)
        {
            if (this.IsEnabled())
            {
                using (message.SetThreadActivityId())
                {
                    this.SendResponse();
                }
            }
        }

        [Event(3, Level = EventLevel.Verbose)]
        private void SendResponse() => WriteEvent(3);
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
                using (message.SetThreadActivityId())
                {
                    this.ReceiveMessage();
                }
            }
        }

        [Event(1, Level = EventLevel.Verbose)]
        private void ReceiveMessage() => WriteEvent(1);
    }

    internal static class EventSourceMessageExtensions
    {
        public static ActivityIdScope SetThreadActivityId(this Message message)
        {
            var activityId = message?.TraceContext?.ActivityId;

            if (!(activityId is Guid messageActivityId) || messageActivityId == Guid.Empty)
            {
                return new ActivityIdScope(Guid.Empty, shouldReset: false);
            }

            EventSource.SetCurrentThreadActivityId(messageActivityId, out var oldActivity);
            return new ActivityIdScope(oldActivity, shouldReset: messageActivityId != oldActivity);

        }

        internal readonly ref struct ActivityIdScope
        {
            private readonly Guid oldActivity;
            private readonly bool shouldReset;

            public ActivityIdScope(Guid oldActivity, bool shouldReset)
            {
                this.oldActivity = oldActivity;
                this.shouldReset = shouldReset;
            }

            public void Dispose()
            {
                if (shouldReset)
                {
                    EventSource.SetCurrentThreadActivityId(oldActivity);
                }
            }
        }
    }
}
