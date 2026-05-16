using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Journaling.Json;

namespace Orleans.Journaling;

public static class HostingExtensions
{
    public static ISiloBuilder AddJournalStorage(this ISiloBuilder builder)
    {
        builder.Services.AddOptions<JournaledStateManagerOptions>();
        builder.Services.TryAddScoped<JournaledStateManagerShared>();
        builder.Services.TryAddScoped<IJournaledStateManager, JournaledStateManager>();
        builder.Services.TryAddScoped<IJournaledStateManagerFactory, JournaledStateManagerFactory>();

        // Register JSON as the default format family and keep Orleans binary available for existing data.
        builder.Services.AddJsonJournalFormat(new JsonJournalOptions().SerializerOptions, tryAdd: true);
        TryAddOrleansBinaryJournalingFormat(builder.Services);

        builder.Services.TryAddKeyedScoped(typeof(IDurableDictionary<,>), KeyedService.AnyKey, typeof(DurableDictionary<,>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableList<>), KeyedService.AnyKey, typeof(DurableList<>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableQueue<>), KeyedService.AnyKey, typeof(DurableQueue<>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableSet<>), KeyedService.AnyKey, typeof(DurableSet<>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableValue<>), KeyedService.AnyKey, typeof(DurableValue<>));
        builder.Services.TryAddKeyedScoped(typeof(IPersistentState<>), KeyedService.AnyKey, typeof(DurableState<>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableTaskCompletionSource<>), KeyedService.AnyKey, typeof(DurableTaskCompletionSource<>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableNothing), KeyedService.AnyKey, typeof(DurableNothing));
        return builder;
    }

    private static void TryAddOrleansBinaryJournalingFormat(IServiceCollection services)
    {
        var key = JournalFormatServices.ValidateJournalFormatKey(OrleansBinaryJournalFormat.JournalFormatKey);

        services.TryAddSingleton<OrleansBinaryJournalFormat>();
        services.TryAddKeyedSingleton<IJournalFormat>(key, static (sp, _) => sp.GetRequiredService<OrleansBinaryJournalFormat>());
        services.TryAddSingleton<IJournalFormat>(static sp => sp.GetRequiredService<OrleansBinaryJournalFormat>());

        services.TryAddKeyedSingleton(typeof(IDurableDictionaryCommandCodec<,>), key, typeof(OrleansBinaryDurableDictionaryCommandCodec<,>));
        services.TryAddKeyedSingleton(typeof(IDurableListCommandCodec<>), key, typeof(OrleansBinaryDurableListCommandCodec<>));
        services.TryAddKeyedSingleton(typeof(IDurableQueueCommandCodec<>), key, typeof(OrleansBinaryDurableQueueCommandCodec<>));
        services.TryAddKeyedSingleton(typeof(IDurableSetCommandCodec<>), key, typeof(OrleansBinaryDurableSetCommandCodec<>));
        services.TryAddKeyedSingleton(typeof(IDurableValueCommandCodec<>), key, typeof(OrleansBinaryDurableValueCommandCodec<>));
        services.TryAddKeyedSingleton(typeof(IPersistentStateCommandCodec<>), key, typeof(OrleansBinaryPersistentStateCommandCodec<>));
        services.TryAddKeyedSingleton(typeof(IDurableTaskCompletionSourceCommandCodec<>), key, typeof(OrleansBinaryDurableTaskCompletionSourceCommandCodec<>));
    }
}
