using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainDirectory;
using Orleans.Hosting;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Hosting;

namespace Orleans.TestingHost;

internal class ConfigureDistributedGrainDirectory : ISiloConfigurator
{
#pragma warning disable ORLEANSEXP003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    public void Configure(ISiloBuilder siloBuilder)
    {
        if (siloBuilder.Services.Any(static service => service.ServiceType == typeof(DistributedGrainDirectory)))
        {
            siloBuilder.Services.AddSingleton(
                static _ => new NamedService<IGrainDirectory>(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY));
            siloBuilder.Services.AddKeyedSingleton<IGrainDirectory>(
                GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY,
                static (services, _) => services.GetRequiredService<DistributedGrainDirectory>());
        }
        else
        {
            siloBuilder.AddDistributedGrainDirectory();
        }
    }
#pragma warning restore ORLEANSEXP003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
}