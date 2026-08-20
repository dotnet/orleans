using Orleans.Journaling;

namespace Orleans.DurableMessaging;

internal static class DurableMessagingStateManagerCapabilities
{
    public static void Validate(IJournaledStateManager stateManager)
    {
        if (!stateManager.SupportsRollback)
        {
            throw new InvalidOperationException(
                "Durable messaging requires an IJournaledStateManager implementation with rollback support.");
        }

        if (!stateManager.SupportsObservers)
        {
            throw new InvalidOperationException(
                "Durable messaging requires IJournaledStateManager observer support: SupportsObservers must be true so IJournaledStateManager.RegisterObserver can be used.");
        }
    }
}
