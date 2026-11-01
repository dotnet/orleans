namespace Orleans.Runtime;

/// <summary>
/// Indicates that an observer is not longer reachable.
/// </summary>
[Serializable]
[GenerateSerializer]
#pragma warning disable RCS1194 // Implement exception constructors.
public sealed class ObserverNotReachableException : OrleansException
#pragma warning restore RCS1194 // Implement exception constructors.
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ObserverNotReachableException"/> class.
    /// </summary>
    /// <param name="message">
    /// The exception message.
    /// </param>
    internal ObserverNotReachableException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ObserverNotReachableException"/> class.
    /// </summary>
    /// <param name="message">
    /// The message.
    /// </param>
    /// <param name="innerException">
    /// The inner exception.
    /// </param>
    internal ObserverNotReachableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
