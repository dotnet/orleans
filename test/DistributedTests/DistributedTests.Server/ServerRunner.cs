using DistributedTests.Server.Configurator;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using DistributedTests.Common.MessageChannel;
using Microsoft.Extensions.Logging;
using Azure.Identity;
using DistributedTests.Common;

namespace DistributedTests.Server
{
    public class CommonParameters
    {
        public string ServiceId { get; set; } = null!;
        public string ClusterId { get; set; } = null!;
        public int SiloPort { get; set; }
        public int GatewayPort { get; set; }
        public Uri AzureTableUri { get; set; } = null!;
        public Uri AzureQueueUri { get; set; } = null!;
        public bool ActivationRepartitioning { get; set; }
    }

    public class ServerRunner<T>
    {
        private static readonly TimeSpan AbruptShutdownTimeout = TimeSpan.FromSeconds(5);

        private readonly ISiloConfigurator<T> _siloConfigurator;
        private readonly string _siloName;

        public ServerRunner(ISiloConfigurator<T> siloConfigurator)
        {
            _siloConfigurator = siloConfigurator;
            _siloName = $"{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..5]}";
        }

        public async Task Run(
            CommonParameters commonParameters,
            T configuratorParameters,
            CancellationToken cancellationToken)
        {
            var channel = await Channels.CreateReceiveChannel(
                _siloName,
                commonParameters.ClusterId,
                commonParameters.AzureQueueUri,
                cancellationToken);

            ServerMessage? msg = null;

            while (true)
            {
                using var host = Host
                    .CreateDefaultBuilder()
                    .ConfigureLogging(logging =>
                    {
                        logging.AddFilter("Orleans.Runtime.Placement.Repartitioning", LogLevel.Debug);
                    })
                    .UseOrleans((ctx, siloBuilder) => ConfigureOrleans(siloBuilder, commonParameters, configuratorParameters))
                    .Build();

                await host.StartAsync(cancellationToken);

                if (msg != null)
                {
                    // we did restart the silo
                    await channel.SendAck(msg, cancellationToken);
                    msg = null;
                }

                msg = await channel.WaitForMessage(cancellationToken);

                if (msg.IsGraceful)
                {
                    await host.StopAsync(cancellationToken);
                }
                else
                {
                    using var stopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    stopCancellation.CancelAfter(AbruptShutdownTimeout);
                    try
                    {
                        await host.StopAsync(stopCancellation.Token);
                    }
                    catch (OperationCanceledException) when (
                        stopCancellation.IsCancellationRequested
                        && !cancellationToken.IsCancellationRequested)
                    {
                        // Host disposal completes the abrupt shutdown after the bounded stop attempt.
                    }
                }

                if (!msg.Restart)
                {
                    await channel.SendAck(msg, cancellationToken);
                    break;
                }
            }
        }

        private void ConfigureOrleans(ISiloBuilder siloBuilder, CommonParameters commonParameters, T configuratorParameters)
        {
            siloBuilder
                .Configure<SiloOptions>(options => options.SiloName = _siloName)
                .Configure<ClusterOptions>(options => { options.ClusterId = commonParameters.ClusterId; options.ServiceId = commonParameters.ServiceId; })
                .ConfigureEndpoints(siloPort: commonParameters.SiloPort, gatewayPort: commonParameters.GatewayPort)
                .UseAzureStorageClustering(options => options.TableServiceClient = commonParameters.AzureTableUri.CreateTableServiceClient());

            if (commonParameters.ActivationRepartitioning)
            {
#pragma warning disable ORLEANSEXP001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                siloBuilder.AddActivationRepartitioner();
#pragma warning restore ORLEANSEXP001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            }

            _siloConfigurator.Configure(siloBuilder, configuratorParameters);
        }
    }
}
