namespace Orleans.Journaling;

/// <summary>
/// Creates journaled state managers for journals identified independently of a grain activation.
/// </summary>
public interface IJournaledStateManagerFactory
{
    /// <summary>
    /// Creates a standalone journaled state manager for the provided journal id.
    /// </summary>
    /// <param name="journalId">The journal id.</param>
    /// <returns>The journaled state manager.</returns>
    /// <remarks>
    /// The caller constructs state machines and registers them using
    /// <see cref="IJournaledStateManager.RegisterStateMachine"/> before initializing the manager.
    /// State machine dependencies and their lifetime are supplied by the caller.
    /// Each manager owns its journal processing and resources.
    /// The caller must initialize and asynchronously dispose the returned manager, including when
    /// creating it from within a grain activation.
    /// </remarks>
    IJournaledStateManager CreateStandalone(JournalId journalId);
}
