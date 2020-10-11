using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Orleans.Networking.Shared
{
    internal sealed class SocketSender : SocketSenderReceiverBase
    {
        private List<ArraySegment<byte>> _bufferList;

        public SocketSender(Socket socket, PipeScheduler scheduler) : base(socket, scheduler)
        {
        }

        public SocketAwaitableEventArgs SendAsync(in ReadOnlySequence<byte> buffers)
        {
            if (buffers.IsSingleSegment)
            {
                return SendAsync(buffers.First);
            }

#if NETCOREAPP
            if (!_awaitableEventArgs.Equals(Memory<byte>.Empty))
            {
                _awaitableEventArgs.SetBuffer(null, 0, 0);
            }
#else
            if (!Array.Empty<byte>().Equals(_awaitableEventArgs.Buffer))
            {
                _awaitableEventArgs.SetBuffer(null, 0, 0);
            }
#endif

            _awaitableEventArgs.BufferList = GetBufferList(buffers);

            if (!_socket.SendAsync(_awaitableEventArgs))
            {
                _awaitableEventArgs.Complete();
            }

            return _awaitableEventArgs;
        }

        private SocketAwaitableEventArgs SendAsync(ReadOnlyMemory<byte> memory)
        {
            // The BufferList getter is much less expensive then the setter.
            if (_awaitableEventArgs.BufferList != null)
            {
                _awaitableEventArgs.BufferList = null;
            }

#if NETCOREAPP
            _awaitableEventArgs.SetBuffer(MemoryMarshal.AsMemory(memory));
#else
            var array = memory.GetArray();
            _awaitableEventArgs.SetBuffer(array.Array, array.Offset, array.Count);
#endif

            if (!_socket.SendAsync(_awaitableEventArgs))
            {
                _awaitableEventArgs.Complete();
            }

            return _awaitableEventArgs;
        }

        private List<ArraySegment<byte>> GetBufferList(in ReadOnlySequence<byte> buffer)
        {
            Debug.Assert(!buffer.IsEmpty);
            Debug.Assert(!buffer.IsSingleSegment);

            if (_bufferList == null)
            {
                _bufferList = new List<ArraySegment<byte>>();
            }
            else
            {
                // Buffers are pooled, so it's OK to root them until the next multi-buffer write.
                _bufferList.Clear();
            }

            foreach (var b in buffer)
            {
                _bufferList.Add(b.GetArray());
            }

            return _bufferList;
        }
    }
}
