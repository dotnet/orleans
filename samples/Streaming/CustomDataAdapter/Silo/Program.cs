using Azure.Data.Tables;
using Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Silo;

var secrets = Secrets.TryLoadFromFile();
if (secrets is null)
{
    Console.Error.WriteLine("ERROR: This sample requires Azure Event Hub configuration.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Please create a Secrets.json file with the following structure:");
    Console.Error.WriteLine("""
    {
        "DataConnectionString": "<Azure Storage connection string>",
        "EventHubConnectionString": "<Event Hub connection string>"
    }
    """);
    Console.Error.WriteLine();
    Console.Error.WriteLine("For local development without Azure, use the 'Simple' streaming sample instead,");
    Console.Error.WriteLine("which supports in-memory streaming.");
    return 1;
}

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

void ConfigureSilo(HostBuilderContext context, ISiloBuilder siloBuilder)
{
    siloBuilder
        .UseLocalhostClustering(serviceId: Constants.ServiceId, clusterId: Constants.ServiceId)
        .AddAzureTableGrainStorage(
            "PubSubStore",
            options => options.TableServiceClient = new TableServiceClient(secrets.DataConnectionString))
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
                        options.TableServiceClient = new TableServiceClient(secrets.DataConnectionString);
                        options.PersistInterval = TimeSpan.FromSeconds(10);
                    }));
            });
}
