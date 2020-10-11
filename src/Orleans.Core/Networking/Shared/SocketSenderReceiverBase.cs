using System;
using System.IO.Pipelines;
using System.Net.Sockets;

namespace Orleans.Networking.Shared
{
    internal abstract class SocketSenderReceiverBase : IDisposable
    {
        protected readonly Socket _socket;
        protected readonly SocketAwaitableEventArgs _awaitableEventArgs;

        protected SocketSenderReceiverBase(Socket socket, PipeScheduler scheduler)
        {
            _socket = socket;
            _awaitableEventArgs = new SocketAwaitableEventArgs(scheduler);
        }

        public void Dispose() => _awaitableEventArgs.Dispose();
    }
}
