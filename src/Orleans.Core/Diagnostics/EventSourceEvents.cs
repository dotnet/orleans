using System.Diagnostics.Tracing;

namespace Orleans.Runtime
{
    /// <summary>
    /// Event source for <see cref="CallbackData"/>.
    /// </summary>
    [EventSource(Name = "Microsoft-Orleans-CallBackData")]
    internal sealed class OrleansCallBackDataEvent : EventSource
    {
        public static readonly OrleansCallBackDataEvent Log = new OrleansCallBackDataEvent();

        /// <summary>
        /// Indicates that a request timeout occurred.
        /// </summary>
        /// <param name="message">The message.</param>
        [NonEvent]
        public void OnTimeout(Message message)
        {
            if (this.IsEnabled())
            {
                this.OnTimeout();
            }
        }

        /// <summary>
        /// Indicates that a request timeout occurred.
        /// </summary>
        [Event(1, Level = EventLevel.Warning)]
        private void OnTimeout() => this.WriteEvent(1);

        /// <summary>
        /// Indicates that a target silo failed.
        /// </summary>
        /// <param name="message">A message addressed to the target silo.</param>
        [NonEvent]
        public void OnTargetSiloFail(Message message)
        {
            if (this.IsEnabled())
            {
                this.OnTargetSiloFail();
            }
        }

        /// <summary>
        /// Indicates that a target silo failed.
        /// </summary>
        [Event(2, Level = EventLevel.Warning)]
        private void OnTargetSiloFail() => this.WriteEvent(2);

        /// <summary>
        /// Indicates that a request completed.
        /// </summary>
        [NonEvent]
        public void DoCallback(Message message)
        {
            if (this.IsEnabled())
            {
                this.DoCallback();
            }
        }

        /// <summary>
        /// Indicates that a request completed.
        /// </summary>
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
                this.SendRequest();
            }
        }

        [Event(1, Level = EventLevel.Verbose)]
        private void SendRequest() => this.WriteEvent(1);

        [NonEvent]
        public void ReceiveResponse(Message message)
        {
            if (this.IsEnabled())
            {
                this.ReceiveResponse();
            }
        }

        [Event(2, Level = EventLevel.Verbose)]
        private void ReceiveResponse() => this.WriteEvent(2);

        [NonEvent]
        public void SendResponse(Message message)
        {
            if (this.IsEnabled())
            {
                this.SendResponse();
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
                this.ReceiveMessage();
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
                this.SendRequest();
            }
        }

        [Event(1, Level = EventLevel.Verbose)]
        private void SendRequest() => WriteEvent(1);

        [NonEvent]
        public void ReceiveResponse(Message message)
        {
            if (this.IsEnabled())
            {
                this.ReceiveResponse();
            }
        }

        [Event(2, Level = EventLevel.Verbose)]
        private void ReceiveResponse() => WriteEvent(2);

        [NonEvent]
        public void SendResponse(Message message)
        {
            if (this.IsEnabled())
            {
                this.SendResponse();
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
                this.ReceiveMessage();
            }
        }

        [Event(1, Level = EventLevel.Verbose)]
        private void ReceiveMessage() => WriteEvent(1);
    }
}
