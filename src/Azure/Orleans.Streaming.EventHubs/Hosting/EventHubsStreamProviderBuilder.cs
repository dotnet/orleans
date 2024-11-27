using System;
using System.Collections.Generic;
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
        builder.AddEventHubStreams(name, GetQueueOptionBuilder(configurationSection), GetDefaultCheckpointerBuilder(configurationSection));
    }

    public void Configure(IClientBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.AddEventHubStreams(name, GetQueueOptionBuilder(configurationSection));
    }

    private static Action<EventHubOptions> GetQueueOptionBuilder(IConfigurationSection configurationSection)
    {
        return (EventHubOptions eventHubOptions) =>
        {
            //eventHubOptions.ConfigureEventHubConnection(
            //);

            //optionsBuilder.Configure<IServiceProvider>((options, services) =>
            //{
            //    options.
            //    var queueNames = configurationSection.GetSection("QueueNames")?.Get<List<string>>();
            //    if (queueNames != null)
            //    {
            //        options.QueueNames = queueNames;
            //    }

            //    var visibilityTimeout = configurationSection["MessageVisibilityTimeout"];
            //    if (TimeSpan.TryParse(visibilityTimeout, out var visibilityTimeoutTimeSpan))
            //    {
            //        options.MessageVisibilityTimeout = visibilityTimeoutTimeSpan;
            //    }

            //    var serviceKey = configurationSection["ServiceKey"];
            //    if (!string.IsNullOrEmpty(serviceKey))
            //    {
            //        // Get a client by name.
            //        options.QueueServiceClient = services.GetRequiredKeyedService<QueueServiceClient>(serviceKey);
            //    }
            //    else
            //    {
            //        // Construct a connection multiplexer from a connection string.
            //        var connectionName = configurationSection["ConnectionName"];
            //        var connectionString = configurationSection["ConnectionString"];
            //        if (!string.IsNullOrEmpty(connectionName) && string.IsNullOrEmpty(connectionString))
            //        {
            //            var rootConfiguration = services.GetRequiredService<IConfiguration>();
            //            connectionString = rootConfiguration.GetConnectionString(connectionName);
            //        }

            //        if (!string.IsNullOrEmpty(connectionString))
            //        {
            //            options.QueueServiceClient = Uri.TryCreate(connectionString, UriKind.Absolute, out var uri)
            //                ? new QueueServiceClient(uri)
            //                : new QueueServiceClient(connectionString);
            //        }
            //    }
            //});
        };
    }

    private static Action<AzureTableStreamCheckpointerOptions> GetDefaultCheckpointerBuilder(IConfigurationSection configurationSection)
    {
        return (AzureTableStreamCheckpointerOptions checkpointerOptions) =>
        {

        };
    }
}
