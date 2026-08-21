#nullable enable
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.DurableMessaging;
using Orleans.DurableTasks;
using Orleans.Journaling;
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

    /// <summary>Adds the silo-side durable grain RPC runtime and durable messaging protocol.</summary>
    public static ISiloBuilder AddDurableTasks(
        this ISiloBuilder siloBuilder,
        Action<DurableTaskOptions>? configureOptions = null)
    {
        siloBuilder.AddDurableMessaging();
        var options = siloBuilder.Services.AddOptions<DurableTaskOptions>();
        if (configureOptions is not null)
        {
            options.Configure(configureOptions);
        }
        options.Validate(
            value => value.ResultRetentionPeriod >= TimeSpan.Zero,
            "Durable task result retention must not be negative.");
        siloBuilder.Services.AddDurableTaskSerialization();
        siloBuilder.Services.TryAddSingleton<DurableTaskRequestShared>();
        siloBuilder.Configure<TypeManifestOptions>(
            options =>
            {
                options.AddAllowedAssembly(typeof(Orleans.DurableTasks.DurableTaskResponse).Assembly);
                options.AddAllowedAssembly(typeof(DurableTaskRequest).Assembly);
            });
        siloBuilder.Services.AddSingleton<DurableTaskGrainRuntimeShared>();
        siloBuilder.Services.AddScoped<DurableTaskGrainRuntime>();
        siloBuilder.Services.AddFromExisting<IDurableTaskGrainRuntime, DurableTaskGrainRuntime>();
        siloBuilder.Services.AddScoped<DurableTaskMessageTransport>();
        siloBuilder.Services.AddFromExisting<IDurableTaskMessageTransport, DurableTaskMessageTransport>();
        siloBuilder.Services.AddScoped<DurableTaskMessageHandler>();
        siloBuilder.Services.AddScoped<IInboxHandler>(sp => sp.GetRequiredService<DurableTaskMessageHandler>());
        siloBuilder.Services.AddScoped<DurableTaskGrainParticipant>();
        siloBuilder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IJournaledGrainParticipant, DurableTaskGrainParticipant>());
        siloBuilder.Services.AddKeyedTransient<IGrainExtension>(typeof(IDurableTaskGrainExtension), (sp, _) => sp.GetRequiredService<DurableTaskGrainRuntime>());
        siloBuilder.Services.AddKeyedTransient<IGrainExtension>(typeof(IDurableTaskServer), (sp, _) => sp.GetRequiredService<DurableTaskGrainRuntime>());

        siloBuilder.Services.TryAddSingleton(TimeProvider.System);
        siloBuilder.AddJournaledDurableTaskStorage();
        return siloBuilder;
    }
}
