namespace Orleans.Journaling;

/// <summary>
/// Reports a hook failure after the journal operation and its state acknowledgement or reset succeeded.
/// </summary>
/// <remarks>
/// The journal owner remains usable. The caller handles the failed post-persistence work using
/// the feature's durable recovery protocol, preserving the completed business operation.
/// </remarks>
[GenerateSerializer]
public sealed class JournaledStatePostCommitException : Exception
{
    /// <summary>
    /// Initializes a post-persistence hook failure.
    /// </summary>
    /// <param name="operation">The successfully completed journal operation.</param>
    /// <param name="innerException">The failure or aggregate of failures from after hooks.</param>
    public JournaledStatePostCommitException(JournaledStateOperation operation, Exception innerException)
        : base($"Journal operation '{operation}' completed, but a post-persistence hook failed.", innerException)
    {
        Operation = operation;
    }

    /// <summary>Gets the successfully completed journal operation.</summary>
    [Id(0)]
    public JournaledStateOperation Operation { get; }
}
