namespace Orleans.Journaling;

/// <summary>
/// Creates journaled state managers for journals identified independently of a grain activation.
/// </summary>
public interface IJournaledStateManagerFactory
{
    /// <summary>
    /// Creates a journaled state manager for the provided journal id.
    /// </summary>
    /// <param name="journalId">The journal id.</param>
    /// <returns>The journaled state manager.</returns>
    /// <remarks>
    /// Each manager owns an independent service scope and state registry. Declare states using
    /// <see cref="IDurableStateManager.GetOrAddState{TState}"/> or
    /// <see cref="IJournaledStateManager.RegisterStateMachine"/> before initializing the manager.
    /// The caller must initialize and asynchronously dispose the returned manager.
    /// </remarks>
    IJournaledStateManager Create(JournalId journalId);
}
