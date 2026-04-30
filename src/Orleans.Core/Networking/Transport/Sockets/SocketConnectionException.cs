#nullable enable
using System;

namespace Orleans.Connections.Transport.Sockets;

[Serializable]
public class SocketConnectionException : Exception
{
    public SocketConnectionException()
    {
    }

    public SocketConnectionException(string? message) : base(message)
    {
    }

    public SocketConnectionException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}