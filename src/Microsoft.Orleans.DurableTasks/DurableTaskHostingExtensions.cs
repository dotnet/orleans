using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.DurableTasks;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using Orleans.DurableTasks.Storage;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Serialization.Configuration;

namespace Orleans.Hosting;

public static class DurableTaskHostingExtensions
{
    /// <summary>Adds the client-side durable grain RPC adapter.</summary>
    public static IClientBuilder AddDurableTasks(this IClientBuilder clientBuilder)
    {
        clientBuilder.Services.AddDurableTaskSerialization();
        clientBuilder.Services.TryAddSingleton<DurableTaskRequestShared>();
        clientBuilder.Configure<TypeManifestOptions>(
            options =>
            {
                options.AddAllowedAssembly(typeof(Orleans.DurableTasks.DurableTaskResponse).Assembly);
                options.AddAllowedAssembly(typeof(DurableTaskRequest).Assembly);
            });
        return clientBuilder;
    }

    /// <summary>Adds the silo-side durable grain RPC runtime over Orleans Messaging.</summary>
    /// <remarks>
    /// Messaging requires Durable Jobs, which wakes grains with pending messages, including scheduled delay resumes.
    /// Grains deriving from <see cref="Orleans.Journaling.DurableGrain"/> or implementing <see cref="IMessagingGrain"/>
    /// bind the runtime during activation setup, before journal recovery, and can inject its
    /// <see cref="IDurableTaskManager"/>. Task state can be stored in any journal format;
    /// this method registers the source-generated JSON metadata which the JSON journal format requires for task state and
    /// for <see cref="DurableTaskAddress"/> values which grains journal.
    /// </remarks>
    public static ISiloBuilder AddDurableTasks(
        this ISiloBuilder siloBuilder,
        Action<DurableTaskOptions>? configureOptions = null)
    {
        siloBuilder.AddMessaging();
        siloBuilder.Services.Configure<JsonJournalOptions>(static options =>
        {
            options.AddTypeInfoResolver(DurableTaskJsonContext.Default);
        });
        var options = siloBuilder.Services.AddOptions<DurableTaskOptions>();
        if (configureOptions is not null)
        {
            options.Configure(configureOptions);
        }
        options.Validate(
            value => value.ResultRetentionPeriod >= TimeSpan.Zero,
            "Durable task result retention must not be negative.");
        options.Validate(
            value => value.StalledExecutionRetryInterval > TimeSpan.Zero,
            "The durable task stalled execution retry interval must be positive.");
        siloBuilder.Services.AddDurableTaskSerialization();
        siloBuilder.Services.TryAddSingleton<DurableTaskRequestShared>();
        siloBuilder.Configure<TypeManifestOptions>(
            options =>
            {
                options.AddAllowedAssembly(typeof(Orleans.DurableTasks.DurableTaskResponse).Assembly);
                options.AddAllowedAssembly(typeof(DurableTaskRequest).Assembly);
            });
        siloBuilder.Services.AddSingleton<DurableTaskGrainRuntimeShared>();
        siloBuilder.Services.TryAddSingleton<DurableTaskMessageTypes>();
        siloBuilder.Services.AddScoped<DurableTaskGrainRuntime>();
        siloBuilder.Services.AddFromExisting<IDurableTaskGrainRuntime, DurableTaskGrainRuntime>();
        siloBuilder.Services.AddFromExisting<IDurableTaskManager, DurableTaskGrainRuntime>();
        siloBuilder.Services.AddScoped(static services => new DurableTaskMessageTransport(
            new Lazy<IOutbox>(services.GetRequiredService<IOutbox>),
            services.GetRequiredService<DurableTaskMessageTypes>()));
        siloBuilder.Services.AddFromExisting<IDurableTaskMessageTransport, DurableTaskMessageTransport>();
        siloBuilder.Services.AddScoped<DurableTaskMessageHandler>();
        siloBuilder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureGrainTypeComponents, DurableTaskGrainTypeConfigurator>());
        siloBuilder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IActivationWorkingSetObserver, DurableTaskActivationObserver>());
        siloBuilder.Services.AddKeyedTransient<IGrainExtension>(typeof(IDurableTaskGrainExtension), (sp, _) => sp.GetRequiredService<DurableTaskGrainRuntime>());
        siloBuilder.Services.AddKeyedTransient<IGrainExtension>(typeof(IDurableTaskServer), (sp, _) => sp.GetRequiredService<DurableTaskGrainRuntime>());

        siloBuilder.Services.TryAddSingleton(TimeProvider.System);
        siloBuilder.AddJournaledDurableTaskStorage();
        return siloBuilder;
    }
}
