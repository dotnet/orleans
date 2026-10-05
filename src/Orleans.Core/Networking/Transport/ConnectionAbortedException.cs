using System;
using System.Runtime.Serialization;

namespace Orleans.Connections.Transport;

/// <summary>
/// Indicates that a transport connection was terminated before initialization or a pending operation could complete.
/// </summary>
[Serializable]
public class ConnectionAbortedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionAbortedException"/> class.
    /// </summary>
    public ConnectionAbortedException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionAbortedException"/> class with a message describing the failure.
    /// </summary>
    /// <param name="message">The message describing the failure.</param>
    public ConnectionAbortedException(string? message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionAbortedException"/> class with a message and the underlying cause.
    /// </summary>
    /// <param name="message">The message describing the failure.</param>
    /// <param name="innerException">The exception which caused the connection to be aborted.</param>
    public ConnectionAbortedException(string? message, Exception? innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionAbortedException"/> class from serialized data.
    /// </summary>
    /// <param name="info">The serialized exception data.</param>
    /// <param name="context">The serialization context.</param>
    [Obsolete]
    protected ConnectionAbortedException(SerializationInfo info, StreamingContext context) : base(info, context)
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

    [Obsolete]
    protected ConnectionClosedException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }
}
