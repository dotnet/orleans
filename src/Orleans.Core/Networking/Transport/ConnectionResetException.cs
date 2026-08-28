#nullable enable

using System;
using System.Runtime.Serialization;

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

    [Obsolete]
    protected ConnectionResetException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }
}