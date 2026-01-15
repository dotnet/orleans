using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.Runtime.GrainDirectory;

namespace Orleans.TestingHost;

internal class ConfigureDistributedGrainDirectory : ISiloConfigurator
{
#pragma warning disable ORLEANSEXP003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    public void Configure(ISiloBuilder siloBuilder) => siloBuilder
        .AddDistributedGrainDirectory()
        .ConfigureLogging(logging =>
        {
            logging.AddFilter(typeof(DistributedGrainDirectory).FullName, LogLevel.Debug);
            logging.AddFilter(typeof(GrainDirectoryPartition).FullName, LogLevel.Debug);
        });
#pragma warning restore ORLEANSEXP003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
}