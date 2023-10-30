#nullable enable

using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime.Messaging;

/// <summary>
/// Indicates that a message frame is invalid, either when sending a message or receiving a message.
/// </summary>
[GenerateSerializer]
public sealed class InvalidMessageFrameException : OrleansException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidMessageFrameException"/> class.
    /// </summary>
    public InvalidMessageFrameException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidMessageFrameException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public InvalidMessageFrameException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidMessageFrameException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public InvalidMessageFrameException(string message, Exception innerException) : base(message, innerException)
    {
    }

    protected InvalidMessageFrameException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }
}