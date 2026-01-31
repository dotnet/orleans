using System.Runtime.Serialization;

namespace Orleans.Runtime;

/// <summary>
/// Indicates a lifecycle was canceled, either by request or due to observer error.
/// </summary>
[Serializable]
[GenerateSerializer]
public sealed class ActivationCancelledException : OrleansException
{
    private static string _message = "Activation Cancelled";
    /// <summary>
    /// Initializes a new instance of the <see cref="ActivationCancelledException"/> class.
    /// </summary>
    internal ActivationCancelledException()
        : base(_message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ActivationCancelledException"/> class.
    /// </summary>
    /// <param name="innerException">
    /// The inner exception.
    /// </param>
    internal ActivationCancelledException(Exception innerException)
        : base(_message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ActivationCancelledException"/> class.
    /// </summary>
    /// <param name="info">
    /// The serialization info.
    /// </param>
    /// <param name="context">
    /// The context.
    /// </param>
    /// <exception cref="SerializationException">The class name is <see langword="null" /> or <see cref="P:System.Exception.HResult" /> is zero (0).</exception>
    /// <exception cref="ArgumentNullException"><paramref name="info" /> is <see langword="null" />.</exception>
    [Obsolete]
    private ActivationCancelledException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
    }
}
