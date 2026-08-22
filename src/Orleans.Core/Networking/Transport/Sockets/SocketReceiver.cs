// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Orleans.Connections.Transport.Sockets;

internal sealed class SocketReceiver : SocketAwaitableEventArgs
{
    public SocketReceiver()
    {
    }

    public ValueTask ReceiveAsync(Socket socket, List<ArraySegment<byte>> buffers)
    {
        BufferList = buffers;

        if (socket.ReceiveAsync(this))
        {
            return new ValueTask(this, 0);
        }

        return Error is not null ? ValueTask.FromException(Error) : default;
    }
}
