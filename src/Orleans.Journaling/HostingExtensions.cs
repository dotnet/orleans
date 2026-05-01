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
        builder.Services
            .TryAddJournalingFormatFamily(OrleansBinaryLogFormat.LogFormatKey)
            .AddLogFormat(OrleansBinaryLogFormat.Instance)
            .AddOperationCodecProvider<OrleansBinaryOperationCodecProvider>();

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
