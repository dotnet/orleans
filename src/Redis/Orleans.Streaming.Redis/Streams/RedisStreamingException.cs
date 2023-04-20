using System;
using System.Runtime.Serialization;

namespace Orleans.Streaming.Redis;

/// <summary>
/// Exception thrown from <see cref="RedisStreamingException"/>.
/// </summary>
[GenerateSerializer]
public class RedisStreamingException : Exception
{
    /// <summary>
    /// Initializes a new instance of <see cref="RedisStreamingException"/>.
    /// </summary>
    public RedisStreamingException()
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="RedisStreamingException"/>.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    public RedisStreamingException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="RedisStreamingException"/>.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="inner">The exception that is the cause of the current exception, or a null reference (Nothing in Visual Basic) if no inner exception is specified.</param>
    public RedisStreamingException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <inheritdoc />
    protected RedisStreamingException(
        SerializationInfo info,
        StreamingContext context)
        : base(info, context)
    {
    }
}