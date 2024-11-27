using System;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;

[assembly: RegisterProvider("EventHubs", "Streaming", "Silo", typeof(EventHubsStreamProviderBuilder))]
[assembly: RegisterProvider("EventHubs", "Streaming", "Client", typeof(EventHubsStreamProviderBuilder))]

namespace Orleans.Hosting;

public sealed class EventHubsStreamProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    public void Configure(ISiloBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.AddEventHubsStreams(
            name,
            GetEventHubOptionsBuilder(name, configurationSection),
            GetEventHubCheckpointerOptionsBuilder(name, configurationSection)
        );
    }

    public void Configure(IClientBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.AddEventHubsStreams(name, GetEventHubOptionsBuilder(name, configurationSection));
    }

    private static Action<OptionsBuilder<EventHubOptions>> GetEventHubOptionsBuilder(string name, IConfigurationSection configurationSection)
    {
        return (OptionsBuilder<EventHubOptions> optionsBuilder) =>
        {
            optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                var serviceKey = configurationSection["ServiceKey"];
                var configuration = services.GetRequiredService<IConfiguration>();

                if (!string.IsNullOrEmpty(serviceKey))
                {
                    // TODO: Get these values from configuration?
                    options.ConfigureEventHubConnection(
                        configuration.GetConnectionString(serviceKey),
                        "events",
                        "silo"
                    );
                }
            });
        };
    }

    private static Action<OptionsBuilder<AzureTableStreamCheckpointerOptions>> GetEventHubCheckpointerOptionsBuilder(string name, IConfigurationSection configurationSection)
    {
        return (OptionsBuilder<AzureTableStreamCheckpointerOptions> optionsBuilder) =>
        {
            optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                var configuration = services.GetRequiredService<IConfiguration>();

                var serviceKey = configurationSection["ServiceKey"];
                if (!string.IsNullOrEmpty(serviceKey))
                {
                    // Get a client by name.
                    options.TableServiceClient = services.GetRequiredKeyedService<TableServiceClient>(serviceKey);
                }
                else
                {

                    // TODO: Grab the keyed table service client
                    var connectionName = configurationSection["CheckpointerConnectionName"];
                    var connectionString = configurationSection["CheckpointerConnectionString"];
                    if (!string.IsNullOrEmpty(connectionName) && string.IsNullOrEmpty(connectionString))
                    {
                        var rootConfiguration = services.GetRequiredService<IConfiguration>();
                        connectionString = rootConfiguration.GetConnectionString(connectionName);
                    }
                    if (!string.IsNullOrEmpty(connectionString))
                    {
                        options.TableServiceClient = Uri.TryCreate(connectionString, UriKind.Absolute, out var uri)
                            ? new TableServiceClient(uri)
                            : new TableServiceClient(connectionString);
                    }
                }
            });
        };
    }
}
