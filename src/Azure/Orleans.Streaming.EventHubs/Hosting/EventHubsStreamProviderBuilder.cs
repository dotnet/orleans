using System;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;

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
                // Load from the root named options, then by aspire options
                // E.g. [ServiceKey]__EventHubName, then Orleans__Streaming__[ServiceKey]__EventHubName
                var namedSection = configuration.GetSection(serviceKey);
                var eventHubName = namedSection["EventHubName"] ?? configuration["EventHubName"];
                var consumerGroup = namedSection["ConsumerGroup"] ?? configuration["ConsumerGroup"];

                if(eventHubName is null)
                {
                    throw new OrleansConfigurationException("No event hub name has been specified. Please provide the Event Hub Name via a root service named EventHubOptions configuration or as part of the Aspire resource configuration.");
                }

                if(consumerGroup is null)
                {
                    throw new OrleansConfigurationException("No consumer group has been specified. Please provide the Consumer Group via a root service named EventHubOptions configuration or as part of the Aspire resource configuration.");
                }

                options.ConfigureEventHubConnection(
                    configuration.GetConnectionString(serviceKey),
                    eventHubName,
                    consumerGroup
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
