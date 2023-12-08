using System;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Storage;

[assembly: RegisterProvider("AzureBlobStorage", "GrainStorage", "Silo", typeof(AzureBlobStorageGrainStorageProviderBuilder))]
namespace Orleans.Hosting;

internal sealed class AzureBlobStorageGrainStorageProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.AddAzureBlobGrainStorage(name, (OptionsBuilder<AzureBlobStorageOptions> optionsBuilder) =>
            optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                var containerName = configurationSection["ContainerName"];
                if (!string.IsNullOrEmpty(containerName))
                {
                    options.ContainerName = containerName; 
                }

                var serviceKey = configurationSection["ServiceKey"];
                if (!string.IsNullOrEmpty(serviceKey))
                {
                    // Get a client by name.
                    options.BlobServiceClient = services.GetRequiredKeyedService<BlobServiceClient>(serviceKey);
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
                            options.BlobServiceClient = new(uri);
                        }
                        else
                        {
                            options.BlobServiceClient = new(connectionString);
                        }
                    }
                }

                var serializerKey = configurationSection["SerializerKey"];
                if (!string.IsNullOrEmpty(serializerKey))
                {
                    options.GrainStorageSerializer = services.GetRequiredKeyedService<IGrainStorageSerializer>(serializerKey);
                }
            }));
    }
}
