using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration.Internal;
using Orleans.DurableMessaging;
using Orleans.DurableTasks;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime.DurableTasks;
using Orleans.Serialization.Configuration;

namespace Orleans.Hosting;

public static class DurableTaskHostingExtensions
{
    public static IClientBuilder AddDurableTasks(this IClientBuilder clientBuilder)
    {
        clientBuilder.Configure<TypeManifestOptions>(
            options => options.AddAllowedAssembly(typeof(System.Distributed.DurableTasks.DurableTaskResponse).Assembly));
        return clientBuilder;
    }

    public static ISiloBuilder AddDurableTasks(this ISiloBuilder siloBuilder)
    {
        siloBuilder.AddDurableMessaging();
        siloBuilder.Configure<TypeManifestOptions>(
            options => options.AddAllowedAssembly(typeof(System.Distributed.DurableTasks.DurableTaskResponse).Assembly));
        siloBuilder.Services.AddSingleton<DurableTaskGrainRuntimeShared>();
        siloBuilder.Services.AddScoped<DurableTaskGrainRuntime>();
        siloBuilder.Services.AddFromExisting<IDurableTaskGrainRuntime, DurableTaskGrainRuntime>();
        siloBuilder.Services.AddScoped<DurableTaskMessageTransport>();
        siloBuilder.Services.AddFromExisting<IDurableTaskMessageTransport, DurableTaskMessageTransport>();
        siloBuilder.Services.AddScoped<DurableTaskMessageHandler>();
        siloBuilder.Services.AddScoped<IInboxHandler>(sp => sp.GetRequiredService<DurableTaskMessageHandler>());
        siloBuilder.Services.AddScoped<DurableTaskGrainParticipant>();
        siloBuilder.Services.AddScoped<IJournaledGrainParticipant>(
            static serviceProvider => serviceProvider.GetRequiredService<DurableTaskGrainParticipant>());
        siloBuilder.Services.AddKeyedTransient<IGrainExtension>(typeof(IDurableTaskGrainExtension), (sp, _) => sp.GetRequiredService<DurableTaskGrainRuntime>());
        siloBuilder.Services.AddKeyedTransient<IGrainExtension>(typeof(IDurableTaskServer), (sp, _) => sp.GetRequiredService<DurableTaskGrainRuntime>());

        siloBuilder.Services.TryAddSingleton(TimeProvider.System);
        return siloBuilder;
    }
}
