using Microsoft.Extensions.DependencyInjection;

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

        var scope = shared.ServiceProvider.CreateAsyncScope();
        try
        {
            var binding = scope.ServiceProvider.GetRequiredService<JournaledStateManagerBinding>();
            var manager = new JournaledStateManager(
                shared,
                JournaledStateManager.CreateStorage(storageProvider, journalId),
                scope.ServiceProvider,
                scope);
            binding.Manager = manager;
            return manager;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }
}
