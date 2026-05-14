// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Orleans.Connections.Transport.Sockets;

internal sealed class SocketSender : SocketAwaitableEventArgs
{
    private List<ArraySegment<byte>>? _bufferList;

    public SocketSender()
    {
    }

    public ValueTask SendAsync(Socket socket, in ReadOnlySequence<byte> buffers)
    {
        if (buffers.IsSingleSegment)
        {
            return SendAsync(socket, buffers.First);
        }

        SetBufferList(buffers);

        if (socket.SendAsync(this))
        {
            return new ValueTask(this, 0);
        }

        return Error is not null ? ValueTask.FromException(Error) : default;
    }

    public ValueTask SendAsync(Socket socket, List<ArraySegment<byte>> buffers)
    {
        BufferList = buffers;

        if (socket.SendAsync(this))
        {
            return new ValueTask(this, 0);
        }

        return Error is not null ? ValueTask.FromException(Error) : default;
    }

    public void Reset()
    {
        // We clear the buffer and buffer list before we put it back into the pool
        // it's a small performance hit but it removes the confusion when looking at dumps to see this still
        // holds onto the buffer when it's back in the pool
        if (BufferList != null)
        {
            BufferList = null;

            _bufferList?.Clear();
        }
        else
        {
            SetBuffer(null, 0, 0);
        }
    }

    public ValueTask SendAsync(Socket socket, ReadOnlyMemory<byte> memory)
    {
        SetBuffer(MemoryMarshal.AsMemory(memory));

        if (socket.SendAsync(this))
        {
            return new ValueTask(this, 0);
        }

        return Error is not null ? ValueTask.FromException(Error) : default;
    }

    private void SetBufferList(in ReadOnlySequence<byte> buffer)
    {
        Debug.Assert(!buffer.IsEmpty);
        Debug.Assert(!buffer.IsSingleSegment);

        if (_bufferList == null)
        {
            _bufferList = new List<ArraySegment<byte>>();
        }

        foreach (var b in buffer)
        {
            _bufferList.Add(b.GetArray());
        }

        // The act of setting this list, sets the buffers in the internal buffer list
        BufferList = _bufferList;
    }
}