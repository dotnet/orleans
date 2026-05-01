using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;

namespace Orleans.Journaling;

public static class HostingExtensions
{
    public static ISiloBuilder AddLogStorage(this ISiloBuilder builder)
    {
        builder.Services.AddOptions<LogManagerOptions>();
        builder.Services.TryAddKeyedScoped<string>(LogFormatServices.LogFormatKeyServiceKey, static (sp, _) => GetLogFormatKey(sp));
        builder.Services.TryAddScoped<ILogStorage>(sp => sp.GetRequiredService<ILogStorageProvider>().Create(sp.GetRequiredService<IGrainContext>()));
        builder.Services.TryAddScoped<ILogManager, LogManager>();

        // Register the default data codec (Orleans IFieldCodec adapter).
        builder.Services.TryAddSingleton(typeof(ILogValueCodec<>), typeof(OrleansLogValueCodec<>));
        builder.Services.TryAddKeyedSingleton<ILogFormat>(OrleansBinaryLogFormat.LogFormatKey, static (_, _) => OrleansBinaryLogFormat.Instance);
        builder.Services.TryAddSingleton<ILogFormat>(_ => OrleansBinaryLogFormat.Instance);

        // Register the binary codec providers for each durable type.
        // Each durable type injects its specific provider interface and calls GetCodec<...>() with
        // its known type arguments, avoiding reflection (MakeGenericType/GetGenericTypeDefinition).
        builder.Services.TryAddSingleton<OrleansBinaryOperationCodecProvider>();
        builder.Services.TryAddKeyedSingleton<IDurableDictionaryOperationCodecProvider>(OrleansBinaryLogFormat.LogFormatKey, static (sp, _) => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        builder.Services.TryAddKeyedSingleton<IDurableListOperationCodecProvider>(OrleansBinaryLogFormat.LogFormatKey, static (sp, _) => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        builder.Services.TryAddKeyedSingleton<IDurableQueueOperationCodecProvider>(OrleansBinaryLogFormat.LogFormatKey, static (sp, _) => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        builder.Services.TryAddKeyedSingleton<IDurableSetOperationCodecProvider>(OrleansBinaryLogFormat.LogFormatKey, static (sp, _) => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        builder.Services.TryAddKeyedSingleton<IDurableValueOperationCodecProvider>(OrleansBinaryLogFormat.LogFormatKey, static (sp, _) => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        builder.Services.TryAddKeyedSingleton<IDurableStateOperationCodecProvider>(OrleansBinaryLogFormat.LogFormatKey, static (sp, _) => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        builder.Services.TryAddKeyedSingleton<IDurableTaskCompletionSourceOperationCodecProvider>(OrleansBinaryLogFormat.LogFormatKey, static (sp, _) => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        builder.Services.TryAddSingleton<IDurableDictionaryOperationCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        builder.Services.TryAddSingleton<IDurableListOperationCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        builder.Services.TryAddSingleton<IDurableQueueOperationCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        builder.Services.TryAddSingleton<IDurableSetOperationCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        builder.Services.TryAddSingleton<IDurableValueOperationCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        builder.Services.TryAddSingleton<IDurableStateOperationCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());
        builder.Services.TryAddSingleton<IDurableTaskCompletionSourceOperationCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryOperationCodecProvider>());

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

    private static string GetLogFormatKey(IServiceProvider serviceProvider)
    {
        var grainContext = serviceProvider.GetRequiredService<IGrainContext>();
        var logFormatKeyProvider = serviceProvider.GetService<ILogFormatKeyProvider>();
        if (logFormatKeyProvider is null && serviceProvider.GetService<ILogStorageProvider>() is ILogFormatKeyProvider storageProvider)
        {
            logFormatKeyProvider = storageProvider;
        }

        return logFormatKeyProvider?.GetLogFormatKey(grainContext) ?? OrleansBinaryLogFormat.LogFormatKey;
    }
}
