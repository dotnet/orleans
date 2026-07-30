using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Persistence.Cosmos;
using StackExchange.Redis;

namespace Orleans.Docs.Snippets.Persistence;

public static class StorageConfiguration
{
    public static void ConfigureManagedIdentity(IConfiguration configuration)
    {
        // <configure_managed_identity>
        var tableEndpoint = new Uri(configuration["AZURE_TABLE_STORAGE_ENDPOINT"]!);
        var blobEndpoint = new Uri(configuration["AZURE_BLOB_STORAGE_ENDPOINT"]!);
        var credential = new DefaultAzureCredential();

        var builder = Host.CreateApplicationBuilder();
        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.AddAzureTableGrainStorage(
                name: "profileStore",
                configureOptions: options =>
                {
                    options.TableServiceClient = new TableServiceClient(tableEndpoint, credential);
                })
                .AddAzureBlobGrainStorage(
                    name: "cartStore",
                    configureOptions: options =>
                    {
                        options.BlobServiceClient = new BlobServiceClient(blobEndpoint, credential);
                    });
        });

        using var host = builder.Build();
        // </configure_managed_identity>
    }

    public static void ConfigureConnectionString()
    {
        // <configure_connection_string>
        var builder = Host.CreateApplicationBuilder();
        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.AddAzureTableGrainStorage(
                name: "profileStore",
                configureOptions: options =>
                {
                    options.TableServiceClient = new TableServiceClient(
                        "DefaultEndpointsProtocol=https;AccountName=data1;AccountKey=SOMETHING1");
                })
                .AddAzureBlobGrainStorage(
                    name: "cartStore",
                    configureOptions: options =>
                    {
                        options.BlobServiceClient = new BlobServiceClient(
                            "DefaultEndpointsProtocol=https;AccountName=data2;AccountKey=SOMETHING2");
                    });
        });

        using var host = builder.Build();
        // </configure_connection_string>
    }

    public static void ConfigureRedis()
    {
        // <configure_redis>
        var builder = Host.CreateApplicationBuilder();
        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.AddRedisGrainStorage(
                name: "redis",
                configureOptions: options =>
                {
                    options.ConfigurationOptions = new ConfigurationOptions
                    {
                        EndPoints = { "localhost:6379" },
                        AbortOnConnectFail = false
                    };
                });
        });

        using var host = builder.Build();
        // </configure_redis>
    }

    public static void ConfigureRedisDefault(ISiloBuilder siloBuilder)
    {
        // <configure_redis_default>
        siloBuilder.AddRedisGrainStorageAsDefault(options =>
        {
            options.ConfigurationOptions = new ConfigurationOptions
            {
                EndPoints = { "localhost:6379" }
            };
        });
        // </configure_redis_default>
    }

    public static void ConfigureRedisAspireAppHost()
    {
        // Note: This is pseudo-code for Aspire AppHost - won't actually compile
        // in this project but represents the pattern for docs
    }

    public static void ConfigureRedisAspireSilo(HostApplicationBuilder builder)
    {
        // <configure_redis_aspire_silo>
        // Register the Redis client with keyed services.
        // Orleans providers look up resources by their keyed service name.
        // builder.AddKeyedRedisClient("orleans-redis");

        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.AddRedisGrainStorage(
                name: "redis",
                configureOptions: options =>
                {
                    // Use the Aspire-provided connection string
                    var connectionString = builder.Configuration.GetConnectionString("orleans-redis");
                    options.ConfigurationOptions = ConfigurationOptions.Parse(connectionString!);
                });
        });
        // </configure_redis_aspire_silo>
    }

    public static void ConfigureRedisAdvanced(ISiloBuilder siloBuilder, IServiceProvider sp)
    {
        // <configure_redis_advanced>
        // Register the Redis client with keyed services.
        // Orleans providers look up resources by their keyed service name.
        // builder.AddKeyedRedisClient("orleans-redis");

        siloBuilder.AddRedisGrainStorage("redis");
        siloBuilder.Services.AddOptions<Orleans.Persistence.RedisStorageOptions>("redis")
            .Configure<IServiceProvider>((options, sp) =>
            {
                options.CreateMultiplexer = _ =>
                {
                    // Resolve the IConnectionMultiplexer from DI (provided by Aspire)
                    return Task.FromResult(sp.GetRequiredService<IConnectionMultiplexer>());
                };
            });
        // </configure_redis_advanced>
    }

    public static void ConfigureCosmosDb()
    {
        // <configure_cosmos>
        var builder = Host.CreateApplicationBuilder();
        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.AddCosmosGrainStorage(
                name: "cosmos",
                configureOptions: options =>
                {
                    options.ConfigureCosmosClient(
                        "https://myaccount.documents.azure.com:443/",
                        new DefaultAzureCredential());
                    options.DatabaseName = "Orleans";
                    options.ContainerName = "OrleansStorage";
                    options.IsResourceCreationEnabled = true;
                });
        });
        // </configure_cosmos>
    }

    public static void ConfigureCosmosDbDefault(ISiloBuilder siloBuilder)
    {
        // <configure_cosmos_default>
        siloBuilder.AddCosmosGrainStorageAsDefault(options =>
        {
            options.ConfigureCosmosClient(
                "https://myaccount.documents.azure.com:443/",
                new DefaultAzureCredential());
            options.IsResourceCreationEnabled = true;
        });
        // </configure_cosmos_default>
    }
}
