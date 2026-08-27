// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Orleans.Serialization.Buffers;

namespace Orleans.Connections.Transport.Sockets;

internal interface ISocketReceiver : IDisposable
{
    int BytesTransferred { get; }
    SocketError SocketError { get; }
    Exception? Error { get; }
    bool HasError { get; }
    ValueTask ReceiveAsync(Socket socket, List<ArraySegment<byte>> buffers);
    ValueTask StopAsync();
}

internal interface IOwnedPageSocketReceiver : ISocketReceiver
{
    ValueTask ReceiveAsync(Socket socket, ArcBufferWriter writer);
}

internal sealed class SocketReceiver : SocketAwaitableEventArgs, ISocketReceiver
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

        var error = Error;
        return error is not null ? ValueTask.FromException(error) : default;
    }

    public ValueTask StopAsync() => default;
}
