using System;
using System.Collections.Generic;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;

[assembly: RegisterProvider("AzureQueueStorage", "Streaming", "Silo", typeof(AzureQueueStreamProviderBuilder))]

namespace Orleans.Hosting;

public sealed class AzureQueueStreamProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.AddAzureQueueStreams(name, (OptionsBuilder<AzureQueueOptions> optionsBuilder) =>
            optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                var queueNames = configurationSection.GetSection("QueueNames")?.Get<List<string>>();
                if (queueNames != null)
                {
                    options.QueueNames = queueNames;
                }

                var serviceKey = configurationSection["ServiceKey"];
                if (!string.IsNullOrEmpty(serviceKey))
                {
                    // Get a client by name.
                    options.QueueServiceClient = services.GetRequiredKeyedService<QueueServiceClient>(serviceKey);
                }
                else
                {
                    // Construct a connection multiplexer from a connection string.
                    var connectionName = configurationSection["ConnectionName"];
                    var connectionString = configurationSection["ConnectionString"];
                    if (!string.IsNullOrEmpty(connectionName) && string.IsNullOrEmpty(connectionString))
                    {
                        var rootConfiguration = services.GetRequiredService<IConfiguration>();
                        connectionString = rootConfiguration.GetConnectionString(connectionName);
                    }

                    if (!string.IsNullOrEmpty(connectionString))
                    {
                        if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
                        {
                            options.QueueServiceClient = new QueueServiceClient(uri);
                        }
                        else
                        {
                            options.QueueServiceClient = new QueueServiceClient(connectionString);
                        }
                    }
                }
            }));
    }
}
