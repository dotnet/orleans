#nullable enable
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration.Internal;
using Orleans.DurableTasks;
using Orleans.Hosting;

namespace Orleans.Runtime.DurableTasks;

public static class DurableTaskHostingExtensions
{
    public static ISiloBuilder AddDurableTasks(this ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.AddSingleton<DurableTaskGrainRuntimeShared>();
        siloBuilder.Services.AddScoped<DurableTaskGrainRuntime>();
        siloBuilder.Services.AddFromExisting<IDurableTaskGrainRuntime, DurableTaskGrainRuntime>();
        siloBuilder.Services.AddKeyedTransient<IGrainExtension>(typeof(IDurableTaskGrainExtension), (sp, _) => sp.GetRequiredService<DurableTaskGrainRuntime>());
        siloBuilder.Services.AddKeyedTransient<IGrainExtension>(typeof(IDurableTaskServer), (sp, _) => sp.GetRequiredService<DurableTaskGrainRuntime>());
        siloBuilder.Services.AddKeyedTransient<IGrainExtension>(typeof(IDurableTaskObserver), (sp, _) => sp.GetRequiredService<DurableTaskGrainRuntime>());

        siloBuilder.Services.TryAddSingleton(TimeProvider.System);
        return siloBuilder;
    }
}
