namespace Orleans.Journaling;

/// <summary>
/// Reports a state preparation failure which prevented a journal write.
/// </summary>
/// <remarks>
/// Pending changes remain staged and safe to commit. The caller can restore the prerequisite
/// and explicitly retry persistence, or retire its owner and recover from durable state.
/// </remarks>
[GenerateSerializer]
public sealed class JournaledStatePreCommitException : Exception
{
    /// <summary>
    /// Initializes a state preparation failure.
    /// </summary>
    /// <param name="innerException">The original preparation failure.</param>
    public JournaledStatePreCommitException(Exception innerException)
        : base("The journal write was prevented by a state preparation failure.", innerException)
    {
    }
}
