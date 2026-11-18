namespace Orleans.Journaling;

internal sealed class JournaledStateManagerFactory(
    JournaledStateManagerShared shared,
    IJournalStorageProvider storageProvider) : IJournaledStateManagerFactory
{
    public IJournaledStateManager CreateStandalone(JournalId journalId)
        => new JournaledStateManager(shared, storageProvider, journalId);
}
