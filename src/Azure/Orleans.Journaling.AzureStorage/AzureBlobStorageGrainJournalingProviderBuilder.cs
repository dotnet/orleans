using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Providers;

[assembly: RegisterProvider("AzureBlobStorage", "GrainJournaling", "Silo", typeof(AzureBlobStorageGrainJournalingProviderBuilder))]
namespace Orleans.Hosting;

internal sealed class AzureBlobStorageGrainJournalingProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.AddAzureAppendBlobStateMachineStorage();
        var optionsBuilder = builder.Services.AddOptions<AzureAppendBlobStateMachineStorageOptions>();
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
        });
    }
}
