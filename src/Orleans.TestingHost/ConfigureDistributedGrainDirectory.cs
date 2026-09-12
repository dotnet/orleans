using Orleans.Hosting;

namespace Orleans.TestingHost;

internal class ConfigureDistributedGrainDirectory : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder) => siloBuilder.AddDistributedGrainDirectory();
}
