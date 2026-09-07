using Orleans.Journaling;

namespace Orleans.DurableMessaging;

internal static class DurableMessagingStateManagerCapabilities
{
    public static void RegisterObserver(IJournaledStateManager stateManager, IJournaledStateObserver observer)
    {
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
