#nullable enable

using System;

namespace Orleans.Connections.Transport;

[Serializable]
public class ConnectionResetException : Exception
{
    public ConnectionResetException()
    {
    }

    public ConnectionResetException(string? message) : base(message)
    {
    }

    public ConnectionResetException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}