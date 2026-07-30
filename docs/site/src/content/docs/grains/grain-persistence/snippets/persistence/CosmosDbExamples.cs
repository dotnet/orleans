using Azure.Identity;
using Orleans.Hosting;
using Orleans.Persistence.Cosmos;
using Orleans.Runtime;

namespace Orleans.Docs.Snippets.Persistence;

// <custom_partition_key_provider>
public class MyPartitionKeyProvider : IPartitionKeyProvider
{
    public ValueTask<string> GetPartitionKey(string grainType, GrainId grainId)
    {
        // Custom logic to determine partition key
        return new ValueTask<string>(grainId.Key.ToString()!);
    }
}
// </custom_partition_key_provider>

public static class CosmosDbConfiguration
{
    public static void ConfigureWithCustomPartitionKey(ISiloBuilder siloBuilder)
    {
        // <configure_cosmos_partition_key>
        // Register with custom partition key provider
        siloBuilder.AddCosmosGrainStorage<MyPartitionKeyProvider>(
            name: "cosmos",
            configureOptions: options =>
            {
                options.ConfigureCosmosClient("https://myaccount.documents.azure.com:443/", new DefaultAzureCredential());
            });
        // </configure_cosmos_partition_key>
    }
}
