namespace Orleans.DurableJobs;

/// <summary>
/// Service key used to resolve the durable jobs subsystem's <see cref="System.TimeProvider"/> from keyed dependency
/// injection. See <see cref="Orleans.Runtime.TimeProviderNames"/> for the core runtime areas.
/// </summary>
public static class DurableJobTimeProviderNames
{
    /// <summary>
    /// The clock used by the durable jobs subsystem.
    /// </summary>
    public const string DurableJobs = "Orleans.DurableJobs";
}
