using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Journaling.Json;

namespace Orleans.Journaling;

public static class HostingExtensions
{
    public static ISiloBuilder AddJournalStorage(this ISiloBuilder builder)
    {
        builder.Services.AddOptions<StateMachineManagerOptions>();
        builder.Services.TryAddKeyedScoped<string>(JournalFormatServices.JournalFormatKeyServiceKey, static (sp, _) => GetJournalFormatKey(sp));
        builder.Services.TryAddScoped<IJournalStorage>(sp => sp.GetRequiredService<IJournalStorageProvider>().Create(sp.GetRequiredService<IGrainContext>()));
        builder.Services.TryAddScoped<IStateMachineManager, JournalStateMachineManager>();

        // Register the default data codec (Orleans IFieldCodec adapter).
        builder.Services.TryAddSingleton(typeof(IJournalValueCodec<>), typeof(OrleansJournalValueCodec<>));

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

        services.TryAddKeyedSingleton<IJournalFormat>(key, OrleansBinaryJournalFormat.Instance);
        services.TryAddSingleton<IJournalFormat>(OrleansBinaryJournalFormat.Instance);

        services.TryAddSingleton<OrleansBinaryOperationCodecProvider>();
        services.TryAddKeyedSingleton<IDurableDictionaryOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        services.TryAddKeyedSingleton<IDurableListOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        services.TryAddKeyedSingleton<IDurableQueueOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        services.TryAddKeyedSingleton<IDurableSetOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        services.TryAddKeyedSingleton<IDurableValueOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        services.TryAddKeyedSingleton<IDurableStateOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        services.TryAddKeyedSingleton<IDurableTaskCompletionSourceOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        services.TryAddSingleton<IDurableDictionaryOperationCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        services.TryAddSingleton<IDurableListOperationCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        services.TryAddSingleton<IDurableQueueOperationCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        services.TryAddSingleton<IDurableSetOperationCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        services.TryAddSingleton<IDurableValueOperationCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        services.TryAddSingleton<IDurableStateOperationCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        services.TryAddSingleton<IDurableTaskCompletionSourceOperationCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
    }

    private static string GetJournalFormatKey(IServiceProvider serviceProvider)
    {
        var grainContext = serviceProvider.GetRequiredService<IGrainContext>();
        var journalFormatKeyProvider = serviceProvider.GetService<IJournalFormatKeyProvider>();
        if (journalFormatKeyProvider is null && serviceProvider.GetService<IJournalStorageProvider>() is IJournalFormatKeyProvider storageProvider)
        {
            journalFormatKeyProvider = storageProvider;
        }

        return journalFormatKeyProvider?.GetJournalFormatKey(grainContext) ?? JsonJournalExtensions.JournalFormatKey;
    }
}
