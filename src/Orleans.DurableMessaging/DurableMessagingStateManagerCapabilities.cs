using Orleans.Journaling;

namespace Orleans.DurableMessaging;

internal static class DurableMessagingStateManagerCapabilities
{
    public static void RegisterObserver(IJournaledStateManager stateManager, IJournaledStateObserver observer)
    {
        if (stateManager is not IJournaledStateMutationRequestSource)
        {
            throw new InvalidOperationException(
                "Durable messaging requires request-time journal mutation guards so inbox handlers cannot commit or delete state before message completion.");
        }

        try
        {
            stateManager.RegisterObserver(observer);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidOperationException(
                "Durable messaging requires IJournaledStateManager observer support through IJournaledStateManager.RegisterObserver.",
                exception);
        }
    }
}
