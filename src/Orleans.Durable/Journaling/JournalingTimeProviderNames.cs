namespace Orleans.Journaling;

/// <summary>
/// Service key used to resolve the journaling subsystem's <see cref="System.TimeProvider"/> from keyed dependency
/// injection. See <see cref="Orleans.Runtime.TimeProviderNames"/> for the core runtime areas.
/// </summary>
public static class JournalingTimeProviderNames
{
    /// <summary>
    /// The clock used by the journaling subsystem.
    /// </summary>
    public const string Journaling = "Orleans.Journaling";
}
