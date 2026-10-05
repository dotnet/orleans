using System;
using System.Runtime.Serialization;

namespace Orleans.Connections.Transport;

/// <summary>
/// Indicates that a transport connection was terminated by a reset from the remote peer or the underlying network.
/// </summary>
[Serializable]
public class ConnectionResetException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionResetException"/> class.
    /// </summary>
    public ConnectionResetException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionResetException"/> class with a message describing the failure.
    /// </summary>
    /// <param name="message">The message describing the failure.</param>
    public ConnectionResetException(string? message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionResetException"/> class with a message and the underlying cause.
    /// </summary>
    /// <param name="message">The message describing the failure.</param>
    /// <param name="innerException">The exception which caused the connection to be reset.</param>
    public ConnectionResetException(string? message, Exception? innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionResetException"/> class from serialized data.
    /// </summary>
    /// <param name="info">The serialized exception data.</param>
    /// <param name="context">The serialization context.</param>
    [Obsolete]
    protected ConnectionResetException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }
}
