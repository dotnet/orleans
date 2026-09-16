namespace Orleans.Journaling;

/// <summary>
/// Initializes a feature which contributes journaled state to a grain activation.
/// </summary>
/// <remarks>
/// Register implementations as grain-scoped services. The <see cref="DurableGrain"/> constructor
/// resolves all registered participants before invoking their <see cref="Initialize"/> methods
/// in registration order, once per activation. This makes each feature's state available when
/// the state manager recovers the journal during grain lifecycle setup.
/// </remarks>
public interface IJournaledGrainParticipant
{
    /// <summary>
    /// Materializes the feature's grain-scoped services before journal recovery begins.
    /// </summary>
    /// <remarks>
    /// Use this method to resolve and register the feature's journaled state. All participant
    /// constructors have completed when this method runs. A participant construction or
    /// initialization exception propagates to the activation caller and fails activation.
    /// </remarks>
    void Initialize();
}
