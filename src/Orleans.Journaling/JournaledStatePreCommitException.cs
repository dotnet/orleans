namespace Orleans.Journaling;

/// <summary>
/// Reports a persistence prerequisite failure which prevented the journal storage operation.
/// </summary>
/// <remarks>
/// Pending changes remain staged and safe to commit. The caller can restore the prerequisite
/// and explicitly retry persistence, or retire its owner and recover from durable state.
/// </remarks>
[GenerateSerializer]
public sealed class JournaledStatePreCommitException : Exception
{
    /// <summary>
    /// Initializes a persistence prerequisite failure.
    /// </summary>
    /// <param name="operation">The journal operation prevented by the failed prerequisite.</param>
    /// <param name="innerException">The original prerequisite failure.</param>
    public JournaledStatePreCommitException(JournaledStateOperation operation, Exception innerException)
        : base($"Journal operation '{operation}' was prevented by a persistence prerequisite failure.", innerException)
    {
    }
}
