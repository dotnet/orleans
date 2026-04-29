using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;

namespace Orleans.Journaling;

public static class HostingExtensions
{
    public static ISiloBuilder AddStateMachineStorage(this ISiloBuilder builder)
    {
        builder.Services.AddOptions<StateMachineManagerOptions>();
        builder.Services.TryAddScoped<IStateMachineStorage>(sp => sp.GetRequiredService<IStateMachineStorageProvider>().Create(sp.GetRequiredService<IGrainContext>()));
        builder.Services.TryAddScoped<IStateMachineManager, StateMachineManager>();

        // Register the default data codec (Orleans IFieldCodec adapter).
        builder.Services.TryAddSingleton(typeof(ILogDataCodec<>), typeof(OrleansLogDataCodec<>));
        builder.Services.TryAddKeyedSingleton<IStateMachineLogFormat>(StateMachineLogFormatKeys.OrleansBinary, static (_, _) => BinaryLogExtentCodec.Instance);
        builder.Services.TryAddSingleton<IStateMachineLogFormat>(_ => BinaryLogExtentCodec.Instance);

        // Register the binary codec providers for each durable type.
        // Each durable type injects its specific provider interface and calls GetCodec<...>() with
        // its known type arguments, avoiding reflection (MakeGenericType/GetGenericTypeDefinition).
        builder.Services.TryAddSingleton<OrleansBinaryLogEntryCodecProvider>();
        builder.Services.TryAddKeyedSingleton<IDurableDictionaryCodecProvider>(StateMachineLogFormatKeys.OrleansBinary, static (sp, _) => sp.GetRequiredService<OrleansBinaryLogEntryCodecProvider>());
        builder.Services.TryAddKeyedSingleton<IDurableListCodecProvider>(StateMachineLogFormatKeys.OrleansBinary, static (sp, _) => sp.GetRequiredService<OrleansBinaryLogEntryCodecProvider>());
        builder.Services.TryAddKeyedSingleton<IDurableQueueCodecProvider>(StateMachineLogFormatKeys.OrleansBinary, static (sp, _) => sp.GetRequiredService<OrleansBinaryLogEntryCodecProvider>());
        builder.Services.TryAddKeyedSingleton<IDurableSetCodecProvider>(StateMachineLogFormatKeys.OrleansBinary, static (sp, _) => sp.GetRequiredService<OrleansBinaryLogEntryCodecProvider>());
        builder.Services.TryAddKeyedSingleton<IDurableValueCodecProvider>(StateMachineLogFormatKeys.OrleansBinary, static (sp, _) => sp.GetRequiredService<OrleansBinaryLogEntryCodecProvider>());
        builder.Services.TryAddKeyedSingleton<IDurableStateCodecProvider>(StateMachineLogFormatKeys.OrleansBinary, static (sp, _) => sp.GetRequiredService<OrleansBinaryLogEntryCodecProvider>());
        builder.Services.TryAddKeyedSingleton<IDurableTaskCompletionSourceCodecProvider>(StateMachineLogFormatKeys.OrleansBinary, static (sp, _) => sp.GetRequiredService<OrleansBinaryLogEntryCodecProvider>());
        builder.Services.TryAddSingleton<IDurableDictionaryCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryLogEntryCodecProvider>());
        builder.Services.TryAddSingleton<IDurableListCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryLogEntryCodecProvider>());
        builder.Services.TryAddSingleton<IDurableQueueCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryLogEntryCodecProvider>());
        builder.Services.TryAddSingleton<IDurableSetCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryLogEntryCodecProvider>());
        builder.Services.TryAddSingleton<IDurableValueCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryLogEntryCodecProvider>());
        builder.Services.TryAddSingleton<IDurableStateCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryLogEntryCodecProvider>());
        builder.Services.TryAddSingleton<IDurableTaskCompletionSourceCodecProvider>(static sp => sp.GetRequiredService<OrleansBinaryLogEntryCodecProvider>());

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
}
