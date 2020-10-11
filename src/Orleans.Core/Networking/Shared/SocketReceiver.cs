using System;
using System.IO.Pipelines;
using System.Net.Sockets;

namespace Orleans.Networking.Shared
{
    internal sealed class SocketReceiver : SocketSenderReceiverBase
    {
        public SocketReceiver(Socket socket, PipeScheduler scheduler) : base(socket, scheduler)
        {
        }

        public SocketAwaitableEventArgs WaitForDataAsync()
        {
#if NETCOREAPP
            _awaitableEventArgs.SetBuffer(Memory<byte>.Empty);
#else
            _awaitableEventArgs.SetBuffer(Array.Empty<byte>(), 0, 0);
#endif

            if (!_socket.ReceiveAsync(_awaitableEventArgs))
            {
                _awaitableEventArgs.Complete();
            }

            return _awaitableEventArgs;
        }

        public SocketAwaitableEventArgs ReceiveAsync(Memory<byte> buffer)
        {
#if NETCOREAPP
            _awaitableEventArgs.SetBuffer(buffer);
#else
            var array = buffer.GetArray();
            _awaitableEventArgs.SetBuffer(array.Array, array.Offset, array.Count);
#endif

            if (!_socket.ReceiveAsync(_awaitableEventArgs))
            {
                _awaitableEventArgs.Complete();
            }

            return _awaitableEventArgs;
        }
    }
}
