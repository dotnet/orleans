// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;

namespace Orleans.Connections.Transport.Sockets;

internal readonly struct SocketOperationResult
{
    public readonly SocketException? SocketError;

    public readonly int BytesTransferred;

    [MemberNotNullWhen(true, nameof(SocketError))]
    public readonly bool HasError => SocketError != null;

    public SocketOperationResult(int bytesTransferred)
    {
        SocketError = null;
        BytesTransferred = bytesTransferred;
    }

    public SocketOperationResult(SocketException exception)
    {
        SocketError = exception;
        BytesTransferred = 0;
    }
}
