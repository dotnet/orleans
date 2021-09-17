using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Threading.Tasks;
using Distributed.GrainInterfaces;
using Distributed.Silo.Configurator;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Streams;

namespace Distributed.Silo
{
    public class CommonParameters
    {
        public string ServiceId { get; set; }
        public string ClusterId { get; set; }
        public int SiloPort { get; set; }
        public int GatewayPort { get; set; }
        public SecretConfiguration.SecretSource SecretSource {  get; set; }
    }

    public class SiloRunner<T>
    {
        private readonly ISiloConfigurator<T> _siloConfigurator;

        public SiloRunner(ISiloConfigurator<T> siloConfigurator)
        {
            _siloConfigurator = siloConfigurator;
        }

        public async Task Run(CommonParameters commonParameters, T configuratorParameters)
        {
            await Host
                .CreateDefaultBuilder()
                .UseOrleans(siloBuilder => ConfigureOrleans(siloBuilder, commonParameters, configuratorParameters))
                .RunConsoleAsync();
        }

        private void ConfigureOrleans(ISiloBuilder siloBuilder, CommonParameters commonParameters, T configuratorParameters)
        {
            var secrets = SecretConfiguration.Load(commonParameters.SecretSource);

            siloBuilder
                .ConfigureDefaults()
                .Configure<ClusterOptions>(options => { options.ClusterId = commonParameters.ClusterId; options.ServiceId = commonParameters.ServiceId; })
                .ConfigureEndpoints(siloPort: commonParameters.SiloPort, gatewayPort: commonParameters.GatewayPort)
                .UseAzureStorageClustering(options => options.ConnectionString = secrets.ClusteringConnectionString);

            _siloConfigurator.Configure(siloBuilder, configuratorParameters);
        }
    }
}
