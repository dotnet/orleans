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
    /// Each manager owns an independent state registry. Its service scope is created when state services
    /// are first requested, and disposed with the manager. Manually registered states can use the manager
    /// without creating a service scope. Declare states using
    /// <see cref="IDurableStateManager.GetOrAddState{TState}"/> or
    /// <see cref="IJournaledStateManager.RegisterStateMachine"/> before initializing the manager.
    /// The caller must initialize and asynchronously dispose the returned manager, including when
    /// creating it from within a grain activation.
    /// </remarks>
    IJournaledStateManager CreateStandalone(JournalId journalId);
}
