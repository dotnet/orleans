using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Durable.Tasks.Protocol;
using Orleans.Durable.Tasks.Runtime;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.Durable.Tasks;
using Orleans.Hosting;
using Orleans.Durable.Journaling;
using Orleans.Durable.Journaling.Json;
using Orleans.Durable.Tasks.Storage;
using Orleans.Durable.Messaging;
using Orleans.Runtime;
using Orleans.Serialization.Configuration;

namespace Orleans.Hosting;

public static class DurableTaskHostingExtensions
{
    /// <summary>Adds the client-side durable grain RPC adapter.</summary>
    public static IClientBuilder AddDurableTasks(this IClientBuilder clientBuilder)
    {
        clientBuilder.Services.TryAddSingleton<DurableTaskRequestShared>();
        clientBuilder.Configure<TypeManifestOptions>(
            options =>
            {
                options.AddAllowedAssembly(typeof(Orleans.Durable.Tasks.DurableTaskResponse).Assembly);
                options.AddAllowedAssembly(typeof(DurableTaskRequest).Assembly);
            });
        return clientBuilder;
    }

    /// <summary>Adds the silo-side durable grain RPC runtime over Orleans Messaging.</summary>
    /// <remarks>
    /// Messaging requires Durable Jobs, which wakes grains with pending messages, including scheduled delay resumes.
    /// Every grain deriving from <see cref="Orleans.Durable.Journaling.DurableGrain"/> receives an inbox and an outbox,
    /// and binds the runtime during activation setup, before journal recovery, and can inject its
    /// <see cref="IDurableTaskManager"/>. Task state lives in the grain's journal, in any journal format;
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
        options.Validate(
            value => value.RunnableExecutionWakeupDelay > TimeSpan.Zero,
            "The durable task runnable execution wake-up delay must be positive.");
        siloBuilder.Services.TryAddSingleton<DurableTaskRequestShared>();
        siloBuilder.Configure<TypeManifestOptions>(
            options =>
            {
                options.AddAllowedAssembly(typeof(Orleans.Durable.Tasks.DurableTaskResponse).Assembly);
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
        siloBuilder.Services.TryAddKeyedScoped(DurableTaskAccessLogs.StateMachineName, static (services, _) => DurableTaskAccessLogs.Create(services));
        siloBuilder.Services.TryAddScoped<DurableTaskGrainStorage>();
        siloBuilder.Services.AddFromExisting<IDurableTaskGrainStorage, DurableTaskGrainStorage>();
        return siloBuilder;
    }
}
