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
    IJournaledStateManager Create(JournalId journalId);
}

