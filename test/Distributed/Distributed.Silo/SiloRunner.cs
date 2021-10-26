using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Distributed.Common;
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
        private readonly string _siloName;
        private SecretConfiguration _secrets;

        public SiloRunner(ISiloConfigurator<T> siloConfigurator)
        {
            _siloConfigurator = siloConfigurator;
            _siloName = $"{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..5]}";
        }

        public async Task Run(CommonParameters commonParameters, T configuratorParameters)
        {
            _secrets = SecretConfiguration.Load(commonParameters.SecretSource);

            var channel = await Channels.CreateReceiveChannel(_siloName, commonParameters.ClusterId, _secrets);

            SiloMessage msg = null;

            while (true)
            {
                var host = Host
                    .CreateDefaultBuilder()
                    .UseOrleans(siloBuilder => ConfigureOrleans(siloBuilder, commonParameters, configuratorParameters))
                    .Build();

                var hostTask = host.RunAsync();

                if (msg != null)
                {
                    // we did restart the silo
                    await channel.SendAck(msg);
                    msg = null;
                }

                msg = await channel.WaitForMessage(CancellationToken.None);

                await host.StopAsync(new CancellationToken(!msg.IsGraceful));

                if (!msg.Restart)
                {
                    await channel.SendAck(msg);
                    break;
                }
            }
        }

        private void ConfigureOrleans(ISiloBuilder siloBuilder, CommonParameters commonParameters, T configuratorParameters)
        {
            siloBuilder
                .ConfigureDefaults()
                .Configure<SiloOptions>(options => options.SiloName = _siloName)
                .Configure<ClusterOptions>(options => { options.ClusterId = commonParameters.ClusterId; options.ServiceId = commonParameters.ServiceId; })
                .ConfigureEndpoints(siloPort: commonParameters.SiloPort, gatewayPort: commonParameters.GatewayPort)
                .UseAzureStorageClustering(options => options.ConnectionString = _secrets.ClusteringConnectionString);

            _siloConfigurator.Configure(siloBuilder, configuratorParameters);
        }
    }
}
