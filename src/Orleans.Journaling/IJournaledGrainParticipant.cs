namespace Orleans.Journaling;

/// <summary>
/// Initializes a feature which contributes journaled state to a grain activation.
/// </summary>
public interface IJournaledGrainParticipant
{
    /// <summary>
    /// Materializes the feature's grain-scoped services before journal recovery begins.
    /// </summary>
    void Initialize();
}
