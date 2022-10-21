using Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;

using Silo;

try
{
    var host = new HostBuilder()
        .UseOrleans(ConfigureSilo)
        .ConfigureLogging(logging => logging.AddConsole())
        .Build();

    await host.RunAsync();

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

static void ConfigureSilo(HostBuilderContext context, ISiloBuilder siloBuilder)
{
    var secrets = Secrets.LoadFromFile()!;
    siloBuilder
        .UseLocalhostClustering(serviceId: Constants.ServiceId, clusterId: Constants.ServiceId)
        .AddAzureTableGrainStorage(
            "PubSubStore",
            options => options.ConfigureTableServiceClient(secrets.DataConnectionString))
        .AddEventHubStreams(
            Constants.StreamProvider,
            (ISiloEventHubStreamConfigurator configurator) =>
            {
                configurator.ConfigureEventHub(builder => builder.Configure(options =>
                {
                    options.ConfigureEventHubConnection(
                        secrets.EventHubConnectionString,
                        Constants.EHPath,
                        Constants.EHConsumerGroup);

                }));
                // We plug here our custom DataAdapter for Event Hub
                configurator.UseDataAdapter(
                    (sp, n) => ActivatorUtilities.CreateInstance<CustomDataAdapter>(sp));
                configurator.UseAzureTableCheckpointer(
                    builder => builder.Configure(options =>
                    {
                        options.ConfigureTableServiceClient(secrets.DataConnectionString);
                        options.PersistInterval = TimeSpan.FromSeconds(10);
                    }));
            });
}
