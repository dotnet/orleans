#nullable enable
using System;

namespace Orleans.Connections.Transport;

[Serializable]
public class ConnectionAbortedException : Exception
{
    public ConnectionAbortedException()
    {
    }

    public ConnectionAbortedException(string? message) : base(message)
    {
    }

    public ConnectionAbortedException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Indicates that a connection closed normally.
/// </summary>
[Serializable]
public class ConnectionClosedException : Exception
{
    public ConnectionClosedException()
    {
    }

    public ConnectionClosedException(string? message) : base(message)
    {
    }

    public ConnectionClosedException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}