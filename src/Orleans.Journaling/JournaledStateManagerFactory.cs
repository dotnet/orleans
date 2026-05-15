namespace Orleans.Journaling;

internal sealed class JournaledStateManagerFactory(
    JournaledStateManagerShared shared,
    IJournalStorageProvider storageProvider) : IJournaledStateManagerFactory
{
    public IJournaledStateManager Create(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        return new JournaledStateManager(shared, storageProvider, journalId);
    }
}

