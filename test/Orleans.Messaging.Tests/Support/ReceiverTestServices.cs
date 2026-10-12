using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Messaging.Configuration;
using Orleans.Runtime;

namespace Orleans.Messaging.Tests.Support;

internal static class ReceiverTestServices
{
    private static readonly Func<IGrainContext?> ReadCurrentContext = typeof(IGrainContext).Assembly
        .GetType("Orleans.Runtime.RuntimeContext", throwOnError: true)!
        .GetProperty("Current")!.GetMethod!.CreateDelegate<Func<IGrainContext?>>();

    public static IGrainContext? CurrentGrainContext => ReadCurrentContext();

    // Uses the production registration, replacing the application-facing outbox with a journaled capture.
    public static void Add(IServiceCollection services, Action<DurableMessagingOptions> configure)
    {
        services.AddMessaging(configure);
        services.RemoveAll<IOutbox>();
        services.AddScoped<IOutbox>(static sp =>
            new JournaledTestOutbox(
                sp.GetRequiredKeyedService<IDurableDictionary<HierarchicalKey, OutboxMessage>>("test-handler-output")));
    }
}
